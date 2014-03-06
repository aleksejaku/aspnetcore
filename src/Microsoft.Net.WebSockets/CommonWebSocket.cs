﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Net.WebSockets
{
    // https://tools.ietf.org/html/rfc6455
    public class CommonWebSocket : WebSocket
    {
        private readonly static Random Random = new Random();

        private readonly Stream _stream;
        private readonly string _subProtocl;
        private readonly bool _maskOutput;
        private readonly bool _unmaskInput;
        private readonly bool _useZeroMask;
        private readonly SemaphoreSlim _writeLock;

        private WebSocketState _state;

        private WebSocketCloseStatus? _closeStatus;
        private string _closeStatusDescription;

        private bool _outgoingMessageInProgress;

        private byte[] _receiveBuffer;
        private int _receiveOffset;
        private int _receiveCount;

        private FrameHeader _frameInProgress;
        private long _frameBytesRemaining = 0;
        private int? _firstDataOpCode;

        public CommonWebSocket(Stream stream, string subProtocol, int receiveBufferSize, bool maskOutput, bool useZeroMask, bool unmaskInput)
        {
            _stream = stream;
            _subProtocl = subProtocol;
            _state = WebSocketState.Open;
            _receiveBuffer = new byte[receiveBufferSize];
            _maskOutput = maskOutput;
            _useZeroMask = useZeroMask;
            _unmaskInput = unmaskInput;
            _writeLock = new SemaphoreSlim(1);
        }

        public static CommonWebSocket CreateClientWebSocket(Stream stream, string subProtocol, int receiveBufferSize, bool useZeroMask)
        {
            return new CommonWebSocket(stream, subProtocol, receiveBufferSize, maskOutput: true, useZeroMask: useZeroMask, unmaskInput: false);
        }

        public static CommonWebSocket CreateServerWebSocket(Stream stream, string subProtocol, int receiveBufferSize)
        {
            return new CommonWebSocket(stream, subProtocol, receiveBufferSize, maskOutput: false, useZeroMask: false, unmaskInput: true);
        }

        public override WebSocketCloseStatus? CloseStatus
        {
            get { return _closeStatus; }
        }

        public override string CloseStatusDescription
        {
            get { return _closeStatusDescription; }
        }

        public override WebSocketState State
        {
            get { return _state; }
        }

        public override string SubProtocol
        {
            get { return _subProtocl; }
        }

        // https://tools.ietf.org/html/rfc6455#section-5.3
        // The masking key is a 32-bit value chosen at random by the client.
        // When preparing a masked frame, the client MUST pick a fresh masking
        // key from the set of allowed 32-bit values.  The masking key needs to
        // be unpredictable; thus, the masking key MUST be derived from a strong
        // source of entropy, and the masking key for a given frame MUST NOT
        // make it simple for a server/proxy to predict the masking key for a
        // subsequent frame.  The unpredictability of the masking key is
        // essential to prevent authors of malicious applications from selecting
        // the bytes that appear on the wire.  RFC 4086 [RFC4086] discusses what
        // entails a suitable source of entropy for security-sensitive
        // applications.
        private int GetNextMask()
        {
            if (_useZeroMask)
            {
                return 0;
            }
            // TODO: Doesn't include negative numbers so it's only 31 bits, not 32.
            return Random.Next();
        }

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            // TODO: Validate arguments
            // TODO: Check state
            // TODO: Check concurrent writes
            // TODO: Check ping/pong state
            // TODO: Masking
            // TODO: Block close frame?

            await _writeLock.WaitAsync(cancellationToken);

            try
            {
                int mask = GetNextMask();
                FrameHeader frameHeader = new FrameHeader(endOfMessage, _outgoingMessageInProgress ? Constants.OpCodes.ContinuationFrame : GetOpCode(messageType), _maskOutput, mask, buffer.Count);
                ArraySegment<byte> segment = frameHeader.Buffer;
                if (_maskOutput && mask != 0)
                {
                    byte[] maskedFrame = Utilities.MergeAndMask(mask, segment, buffer);
                    await _stream.WriteAsync(maskedFrame, 0, maskedFrame.Length, cancellationToken);
                }
                else
                {
                    await _stream.WriteAsync(segment.Array, segment.Offset, segment.Count, cancellationToken);
                    await _stream.WriteAsync(buffer.Array, buffer.Offset, buffer.Count, cancellationToken);
                }
                _outgoingMessageInProgress = !endOfMessage;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private int GetOpCode(WebSocketMessageType messageType)
        {
            switch (messageType)
            {
                case WebSocketMessageType.Text: return Constants.OpCodes.TextFrame;
                case WebSocketMessageType.Binary: return Constants.OpCodes.BinaryFrame;
                case WebSocketMessageType.Close: return Constants.OpCodes.CloseFrame;
                default: throw new NotImplementedException(messageType.ToString());
            }
        }

        public async override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            // TODO: Validate arguments
            // TODO: Check state
            // TODO: Check concurrent reads
            // TODO: Check ping/pong state

            // No active frame
            while (_frameInProgress == null)
            {
                await EnsureDataAvailableOrReadAsync(2, cancellationToken);
                int frameHeaderSize = FrameHeader.CalculateFrameHeaderSize(_receiveBuffer[_receiveOffset + 1]);
                await EnsureDataAvailableOrReadAsync(frameHeaderSize, cancellationToken);
                _frameInProgress = new FrameHeader(new ArraySegment<byte>(_receiveBuffer, _receiveOffset, frameHeaderSize));
                _receiveOffset += frameHeaderSize;
                _receiveCount -= frameHeaderSize;
                _frameBytesRemaining = _frameInProgress.DataLength;

                if (_unmaskInput != _frameInProgress.Masked)
                {
                    throw new InvalidOperationException("Unmasking settings out of sync with data.");
                }

                // Ping or Pong frames
                if (_frameInProgress.OpCode == Constants.OpCodes.PingFrame || _frameInProgress.OpCode == Constants.OpCodes.PongFrame)
                {
                    // Drain it, should be less than 125 bytes
                    await EnsureDataAvailableOrReadAsync((int)_frameBytesRemaining, cancellationToken);

                    if (_frameInProgress.OpCode == Constants.OpCodes.PingFrame && State == WebSocketState.Open)
                    {
                        await SendPongReply(cancellationToken);
                    }

                    _receiveOffset += (int)_frameBytesRemaining;
                    _receiveCount -= (int)_frameBytesRemaining;
                    _frameBytesRemaining = 0;
                    _frameInProgress = null;
                }
            }

            // Handle fragmentation, remember the first frame type
            int opCode = 0;
            if (_frameInProgress.OpCode == Constants.OpCodes.BinaryFrame
                || _frameInProgress.OpCode == Constants.OpCodes.TextFrame
                || _frameInProgress.OpCode == Constants.OpCodes.CloseFrame)
            {
                opCode = _frameInProgress.OpCode;
                _firstDataOpCode = opCode;
            }
            else if (_frameInProgress.OpCode == Constants.OpCodes.ContinuationFrame)
            {
                if (!_firstDataOpCode.HasValue)
                {
                    throw new InvalidOperationException("A continuation can't be the first frame");
                }
                opCode = _firstDataOpCode.Value;
            }

            if (_frameInProgress.Fin)
            {
                _firstDataOpCode = null;
            }

            WebSocketReceiveResult result;

            if (opCode == Constants.OpCodes.CloseFrame)
            {
                // The close message should be less than 125 bytes and fit in the buffer.
                await EnsureDataAvailableOrReadAsync((int)_frameBytesRemaining, CancellationToken.None);

                // Status code and message are optional
                if (_frameBytesRemaining >= 2)
                {
                    ArraySegment<byte> dataSegment = new ArraySegment<byte>(_receiveBuffer, _receiveOffset + 2, (int)_frameBytesRemaining - 2);
                    if (_unmaskInput)
                    {
                        // In place
                        Utilities.Mask(_frameInProgress.MaskKey, dataSegment);
                    }
                    _closeStatus = (WebSocketCloseStatus)((_receiveBuffer[_receiveOffset] << 8) | _receiveBuffer[_receiveOffset + 1]);
                    _closeStatusDescription = Encoding.UTF8.GetString(dataSegment.Array, dataSegment.Offset, dataSegment.Count) ?? string.Empty;
                }
                else
                {
                    _closeStatus = _closeStatus ?? WebSocketCloseStatus.NormalClosure;
                    _closeStatusDescription = _closeStatusDescription ?? string.Empty;
                }
                result = new WebSocketReceiveResult(0, WebSocketMessageType.Close, true, (WebSocketCloseStatus)_closeStatus, _closeStatusDescription);

                if (State == WebSocketState.Open)
                {
                    _state = WebSocketState.CloseReceived;
                }
                else if (State == WebSocketState.CloseSent)
                {
                    _state = WebSocketState.Closed;
                    _stream.Dispose();
                }
                return result;
            }

            if (_frameBytesRemaining == 0)
            {
                // End of an empty frame?
                result = new WebSocketReceiveResult(0, GetMessageType(opCode), _frameInProgress.Fin);
                _frameInProgress = null;
                return result;
            }

            // Make sure there's at least some data in the buffer
            await EnsureDataAvailableOrReadAsync(1, cancellationToken);
            // Copy buffered data to the users buffer
            int bytesToRead = (int)Math.Min((long)buffer.Count, _frameBytesRemaining);
            int bytesToCopy = Math.Min(bytesToRead, _receiveCount);
            Array.Copy(_receiveBuffer, _receiveOffset, buffer.Array, buffer.Offset, bytesToCopy);
            if (_unmaskInput)
            {
                // TODO: mask alignment may be off between reads.
                Utilities.Mask(_frameInProgress.MaskKey, new ArraySegment<byte>(buffer.Array, buffer.Offset, bytesToCopy));
            }
            if (bytesToCopy == _frameBytesRemaining)
            {
                result = new WebSocketReceiveResult(bytesToCopy, GetMessageType(opCode), _frameInProgress.Fin);
                _frameInProgress = null;
            }
            else
            {
                result = new WebSocketReceiveResult(bytesToCopy, GetMessageType(opCode), false);
            }
            _frameBytesRemaining -= bytesToCopy;
            _receiveCount -= bytesToCopy;
            _receiveOffset += bytesToCopy;

            return result;
        }

        // We received a ping, send a pong in reply
        private async Task SendPongReply(CancellationToken cancellationToken)
        {
            ArraySegment<byte> dataSegment = new ArraySegment<byte>(_receiveBuffer, _receiveOffset, (int)_frameBytesRemaining);
            if (_unmaskInput)
            {
                // In place
                Utilities.Mask(_frameInProgress.MaskKey, dataSegment);
            }

            int mask = GetNextMask();
            FrameHeader header = new FrameHeader(true, Constants.OpCodes.PongFrame, _maskOutput, mask, _frameBytesRemaining);
            if (_maskOutput)
            {
                // In place
                Utilities.Mask(_frameInProgress.MaskKey, dataSegment);
            }

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                ArraySegment<byte> headerSegment = header.Buffer;
                await _stream.WriteAsync(headerSegment.Array, headerSegment.Offset, headerSegment.Count, cancellationToken);
                await _stream.WriteAsync(dataSegment.Array, dataSegment.Offset, dataSegment.Count, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task EnsureDataAvailableOrReadAsync(int bytes, CancellationToken cancellationToken)
        {
            // Insufficient data
            while (_receiveCount < bytes && bytes <= _receiveBuffer.Length)
            {
                // Some data in the buffer, shift down to make room
                if (_receiveCount > 0 && _receiveOffset > 0)
                {
                    Array.Copy(_receiveBuffer, _receiveOffset, _receiveBuffer, 0, _receiveCount);
                }
                _receiveOffset = 0;
                // Add to the end
                int read = await _stream.ReadAsync(_receiveBuffer, _receiveCount, _receiveBuffer.Length - (_receiveCount), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("Unexpected end of stream");
                }
                _receiveCount += read;
            }
        }

        private WebSocketMessageType GetMessageType(int opCode)
        {
            switch (opCode)
            {
                case Constants.OpCodes.TextFrame: return WebSocketMessageType.Text;
                case Constants.OpCodes.BinaryFrame: return WebSocketMessageType.Binary;
                case Constants.OpCodes.CloseFrame: return WebSocketMessageType.Close;
                default: throw new NotImplementedException(opCode.ToString());
            }
        }

        public async override Task CloseAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            // TODO: Validate arguments
            // TODO: Check state
            // TODO: Check concurrent writes
            // TODO: Check ping/pong state

            if (State >= WebSocketState.Closed)
            {
                throw new InvalidOperationException("Already closed.");
            }

            if (State == WebSocketState.Open || State == WebSocketState.CloseReceived)
            {
                // Send a close message.
                await CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
            }

            if (State == WebSocketState.CloseSent)
            {
                // Do a receiving drain
                byte[] data = new byte[1024];
                WebSocketReceiveResult result;
                do
                {
                    result = await ReceiveAsync(new ArraySegment<byte>(data), cancellationToken);
                }
                while (result.MessageType != WebSocketMessageType.Close);

                _closeStatus = result.CloseStatus;
                _closeStatusDescription = result.CloseStatusDescription;
            }

            _state = WebSocketState.Closed;
            _stream.Dispose();
        }

        public override async Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string statusDescription, CancellationToken cancellationToken)
        {
            // TODO: Validate arguments
            // TODO: Check state
            // TODO: Check concurrent writes
            // TODO: Check ping/pong state

            if (State == WebSocketState.CloseSent || State >= WebSocketState.Closed)
            {
                throw new InvalidOperationException("Already closed.");
            }

            if (State == WebSocketState.Open)
            {
                _state = WebSocketState.CloseSent;
            }
            else if (State == WebSocketState.CloseReceived)
            {
                _state = WebSocketState.Closed;
            }

            byte[] descriptionBytes = Encoding.UTF8.GetBytes(statusDescription ?? string.Empty);
            byte[] fullData = new byte[descriptionBytes.Length + 2];
            fullData[0] = (byte)((int)closeStatus >> 8);
            fullData[1] = (byte)closeStatus;
            Array.Copy(descriptionBytes, 0, fullData, 2, descriptionBytes.Length);

            int mask = GetNextMask();
            if (_maskOutput)
            {
                Utilities.Mask(mask, new ArraySegment<byte>(fullData));
            }

            FrameHeader frameHeader = new FrameHeader(true, Constants.OpCodes.CloseFrame, _maskOutput, mask, fullData.Length);
            ArraySegment<byte> segment = frameHeader.Buffer;
            await _stream.WriteAsync(segment.Array, segment.Offset, segment.Count, cancellationToken);
            await _stream.WriteAsync(fullData, 0, fullData.Length, cancellationToken);
        }

        public override void Abort()
        {
            if (_state >= WebSocketState.Closed) // or Aborted
            {
                return;
            }

            _state = WebSocketState.Aborted;
            _stream.Dispose();
        }

        public override void Dispose()
        {
            if (_state >= WebSocketState.Closed) // or Aborted
            {
                return;
            }

            _state = WebSocketState.Closed;
            _stream.Dispose();
        }
    }
}
