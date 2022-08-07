using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Models.PlaybackDtos;
using MediaBrowser.Model.IO;

namespace Jellyfin.Api.Helpers
{
    /// <summary>
    /// A progressive file stream for transferring transcoded files as they are written to.
    /// </summary>
    public class ProgressiveFileStream : Stream
    {
        private readonly Stream _stream;
        private readonly TranscodingJobDto? _job;
        private readonly TranscodingJobHelper? _transcodingJobHelper;
        private readonly int _timeoutMs;
        private bool _disposed;
        private long _estimatedBytes;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressiveFileStream"/> class.
        /// </summary>
        /// <param name="filePath">The path to the transcoded file.</param>
        /// <param name="job">The transcoding job information.</param>
        /// <param name="transcodingJobHelper">The transcoding job helper.</param>
        /// <param name="timeoutMs">The timeout duration in milliseconds.</param>
        public ProgressiveFileStream(string filePath, TranscodingJobDto? job, TranscodingJobHelper transcodingJobHelper, int timeoutMs = 30000)
        {
            _job = job;
            _transcodingJobHelper = transcodingJobHelper;
            _timeoutMs = timeoutMs;

            _stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ProgressiveFileStream"/> class.
        /// </summary>
        /// <param name="stream">The stream to progressively copy.</param>
        /// <param name="timeoutMs">The timeout duration in milliseconds.</param>
        public ProgressiveFileStream(Stream stream, int timeoutMs = 30000)
        {
            _job = null;
            _transcodingJobHelper = null;
            _timeoutMs = timeoutMs;
            _stream = stream;
        }

        /// <inheritdoc />
        public override bool CanRead => _stream.CanRead;

        /// <inheritdoc />
        public override bool CanSeek => true; // _stream.CanSeek;

        /// <inheritdoc />
        public override bool CanWrite => false;

        /// <inheritdoc />
        public override long Length => _estimatedBytes; // throw new NotSupportedException();

        /// <inheritdoc />
        public override long Position
        {
            get => _stream.Position;
            set => _stream.Position = value;
        }

        /// <inheritdoc />
        public override void Flush()
        {
            // Not supported
        }

        /// <inheritdoc />
        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        /// <inheritdoc />
        public override int Read(Span<byte> buffer)
        {
            int totalBytesRead = 0;
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                totalBytesRead += _stream.Read(buffer);
                if (StopReading(totalBytesRead, stopwatch.ElapsedMilliseconds))
                {
                    break;
                }

                Thread.Sleep(50);
            }

            UpdateBytesWritten(totalBytesRead);

            return totalBytesRead;
        }

        /// <inheritdoc />
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

        /// <inheritdoc />
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int totalBytesRead = 0;
            var stopwatch = Stopwatch.StartNew();

            while (true)
            {
                totalBytesRead += await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (StopReading(totalBytesRead, stopwatch.ElapsedMilliseconds))
                {
                    break;
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            UpdateBytesWritten(totalBytesRead);

            return totalBytesRead;
        }

        /// <inheritdoc />
        public override long Seek(long offset, SeekOrigin origin)
        //            => throw new NotSupportedException();
        {
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "offset = {0} origin = {1}", offset, origin));
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "bytes downloaded = {0}", _job.BytesDownloaded));
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "bytes transcoded = {0}", _job.BytesTranscoded));
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "_stream length = {0}", _stream.Length));
            Console.WriteLine(String.Format(CultureInfo.InvariantCulture, "_stream length - offset = {0}", _stream.Length - offset));

            // _job.BytesTranscoded seems to stay empty
            if (origin == SeekOrigin.Begin)
            {
                // if (offset <= (_job.BytesTranscoded ?? 0))
                if (offset <= _stream.Length)
                {
                    this.Position = offset;
                }
                else
                {
                    throw new NotSupportedException();
                }
                // wait if transcode hasn't gotten to the bytes yet?
                // else ?
            }
            return this.Position;
        }

        /// <inheritdoc />
        public override void SetLength(long value)
        {
            _estimatedBytes = value;
        }

        /// <inheritdoc />
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (disposing)
                {
                    _stream.Dispose();

                    if (_job != null)
                    {
                        _transcodingJobHelper?.OnTranscodeEndRequest(_job);
                    }
                }
            }
            finally
            {
                _disposed = true;
                base.Dispose(disposing);
            }
        }

        private void UpdateBytesWritten(int totalBytesRead)
        {
            if (_job != null)
            {
                _job.BytesDownloaded += totalBytesRead;
            }
        }

        private bool StopReading(int bytesRead, long elapsed)
        {
            // It should stop reading when anything has been successfully read or if the job has exited
            // If the job is null, however, it's a live stream and will require user action to close,
            // but don't keep it open indefinitely if it isn't reading anything
            return bytesRead > 0 || (_job?.HasExited ?? elapsed >= _timeoutMs);
        }
    }
}
