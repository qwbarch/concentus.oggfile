using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Concentus.Oggfile
{
    public struct RentedPacket
    {
        /// <summary>
        /// This array is rented via <b>ArrayPool.Shared</b> and must be returned
        /// via <b>ArrayPool.Shared.Return</b> when finished using it.
        /// </summary>
        public byte[] packet;
        public int packetLength;
    }

    /// Provides the same functionality as __OpusOggReadStream__, except that the packets
    /// are rented via __ArrayPool.Shared__ to avoid frequent allocations.
    public class OpusOggRentReadStream
    {
        private const double GranuleSampleRate = 48000.0; // Granule position is always expressed in units of 48000hz
        private readonly Stream _stream;

        private byte[] _nextDataPacket;
        private int _nextDataPacketLength;
        private IPacketProvider _packetProvider;
        private bool _endOfStream;

        /// <summary>
        /// Builds an Ogg file reader that decodes Opus packets from the given input stream, using a 
        /// specified output sample rate and channel count. The given decoder will be used as-is
        /// and return the decoded PCM buffers directly.
        /// </summary>
        /// <param name="stream">The input stream for an Ogg formatted .opus file. The stream will be read from immediately</param>
        public OpusOggRentReadStream(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            _stream = stream;
            _endOfStream = !Initialize();
        }

        /// <summary>
        /// Gets a value indicating whether the current stream supports seeking.
        /// </summary>
        public bool CanSeek => _stream.CanSeek;

        /// <summary>
        /// Gets the tags that were parsed from the OpusTags Ogg packet, or NULL if no such packet was found.
        /// </summary>
        public OpusTags Tags { get; private set; }

        /// <summary>
        /// Returns true if there is still another data packet to be decoded from the current Ogg stream.
        /// Note that this decoder currently only assumes that the input has 1 elementary stream with no splices
        /// or other fancy things.
        /// </summary>
        public bool HasNextPacket => !_endOfStream;

        /// <summary>
        /// If an error happened either in stream initialization, reading, or decoding, the message will appear here.
        /// </summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Gets the position of the last granule in the page the packet is in.
        /// </summary>
        public long PageGranulePosition { get; private set; }

        /// <summary>
        /// Gets the current time in the stream.
        /// </summary>
        public TimeSpan CurrentTime => TimeSpan.FromSeconds((double)PageGranulePosition / GranuleSampleRate);

        /// <summary>
        /// Gets the total number of granules in this stream.
        /// </summary>
        public long GranuleCount { get; private set; }

        /// <summary>
        /// Gets the total time from the stream. Only available if the stream is seekable.
        /// </summary>
        public TimeSpan TotalTime => TimeSpan.FromSeconds(GranuleCount / GranuleSampleRate);

        /// <summary>
        /// Gets the current pages (or frame) position in this stream.
        /// </summary>
        public long PagePosition { get; private set; }

        /// <summary>
        /// Gets the total number of pages (or frames) this stream uses. Only available if the stream is seekable.
        /// </summary>
        public long PageCount { get; private set; }

        /// <summary>
        /// __Note: The returned array must be returned via ArrayPool.Shared.Return__.
        /// 
        /// Reads the next raw Opus packet from the stream.
        /// If there are no more packets to decode, this returns NULL.
        /// </summary>
        /// <returns>The next packet in the stream, or NULL</returns>
        public RentedPacket RentNextRawPacket()
        {
            if (_nextDataPacket == null || _nextDataPacketLength == 0)
            {
                _endOfStream = true;
                return new RentedPacket();
            }

            RentedPacket packet = new RentedPacket
            {
                packet = _nextDataPacket,
                packetLength = _nextDataPacketLength
            };
            QueueNextPacket();
            return packet;
        }

        /// <summary>
        /// Creates an opus decoder and reads from the ogg stream until a data packet is encountered,
        /// queuing it up for future decoding. Tags are also parsed if they are encountered.
        /// </summary>
        /// <returns>True if the stream is valid and ready to be decoded</returns>
        private bool Initialize()
        {
            try
            {
                var oggContainerReader = new OggContainerReader(_stream, true);
                if (!oggContainerReader.Init())
                {
                    LastError = "Could not initialize stream";
                    return false;
                }

                if (oggContainerReader.StreamSerials.Length == 0)
                {
                    LastError = "Initialization failed: No elementary streams found in input file";
                    return false;
                }

                int firstStreamSerial = oggContainerReader.StreamSerials[0];
                _packetProvider = oggContainerReader.GetStream(firstStreamSerial);

                if (CanSeek)
                {
                    GranuleCount = _packetProvider.GetGranuleCount();
                    PageCount = _packetProvider.GetTotalPageCount();
                }

                QueueNextPacket();

                return true;
            }
            catch (Exception e)
            {
                LastError = "Unknown initialization error: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// Seeks the stream for a valid packet at the specified playbackTime. Note that this is the best approximated position.
        /// </summary>
        /// <param name="playbackTime">The playback time.</param>
        public void SeekTo(TimeSpan playbackTime)
        {
            if (!CanSeek)
            {
                throw new InvalidOperationException("Stream is not seekable.");
            }

            if (playbackTime < TimeSpan.Zero || playbackTime > TotalTime)
            {
                throw new ArgumentOutOfRangeException(nameof(playbackTime));
            }

            long granulePosition = Convert.ToInt64(playbackTime.TotalSeconds * GranuleSampleRate);
            SeekToGranulePosition(granulePosition);
        }

        /// <summary>
        /// Seeks the stream for a valid packet at the specified granule position.
        /// </summary>
        /// <param name="granulePosition">The granule position.</param>
        private void SeekToGranulePosition(long granulePosition)
        {
            if (!CanSeek)
            {
                throw new InvalidOperationException("Stream is not seekable.");
            }

            if (granulePosition < 0 || granulePosition > GranuleCount)
            {
                throw new ArgumentOutOfRangeException(nameof(granulePosition));
            }

            // Find a packet based on offset and return 1 in the callback if the packet is valid
            var foundPacket = _packetProvider.FindPacket(granulePosition, GetPacketLength);

            // Check of the found packet is valid
            if (foundPacket == null || foundPacket.IsEndOfStream)
            {
                _endOfStream = true;
                _nextDataPacket = null;
                return;
            }

            // Just seek to this found packet and get the previous packet (preRoll = 1)
            _packetProvider.SeekToPacket(foundPacket, 1);

            // Update the PageGranulePosition to the position from this next packet which will be retrieved by the next QueueNextPacket call
            PageGranulePosition = _packetProvider.PeekNextPacket().PageGranulePosition;
        }

        private int GetPacketLength(DataPacket curPacket, DataPacket lastPacket)
        {
            // if we don't have a previous packet, or we're re-syncing, this packet has no audio data to return
            if (lastPacket == null || curPacket.IsResync)
            {
                return 0;
            }

            // make sure they are audio packets
            if (curPacket.ReadBit())
            {
                return 0;
            }
            if (lastPacket.ReadBit())
            {
                return 0;
            }

            // Just return a value > 0
            return 1;
        }

        /// <summary>
        /// Looks for the next opus data packet in the Ogg stream and queues it up.
        /// If the end of stream has been reached, this does nothing.
        /// </summary>
        private void QueueNextPacket()
        {
            if (_endOfStream)
            {
                return;
            }

            DataPacket packet = _packetProvider.GetNextPacket();
            if (packet == null)
            {
                _endOfStream = true;
                _nextDataPacket = null;
                return;
            }

            PageGranulePosition = packet.PageGranulePosition;
            PagePosition = packet.PageSequenceNumber;

            byte[] buf = ArrayPool<byte>.Shared.Rent(packet.Length);
            packet.Read(buf, 0, packet.Length);
            packet.Done();

            if (packet.Length > 8 && "OpusHead".Equals(Encoding.UTF8.GetString(buf, 0, 8)))
            {
                ArrayPool<byte>.Shared.Return(buf);
                QueueNextPacket();
            }
            else if (packet.Length > 8 && "OpusTags".Equals(Encoding.UTF8.GetString(buf, 0, 8)))
            {
                Tags = OpusTags.ParsePacket(buf, packet.Length);
                ArrayPool<byte>.Shared.Return(buf);
                QueueNextPacket();
            }
            else
            {
                _nextDataPacket = buf;
                _nextDataPacketLength = packet.Length;
            }
        }
    }
}
