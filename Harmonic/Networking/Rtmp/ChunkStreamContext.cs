﻿using Harmonic.Buffers;
using Harmonic.Networking.Amf.Serialization.Amf0;
using Harmonic.Networking.Amf.Serialization.Amf3;
using Harmonic.Networking.Rtmp.Data;
using Harmonic.Networking.Rtmp.Messages;
using Harmonic.Networking.Rtmp.Serialization;
using Harmonic.Networking.Utils;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static Harmonic.Networking.Rtmp.IOPipeLine;

namespace Harmonic.Networking.Rtmp
{
    class ChunkStreamContext : IDisposable
    {
        //private ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;
        internal ChunkHeader _processingChunk = null;
        internal int ReadMinimumBufferSize { get => (ReadChunkSize + TYPE0_SIZE) * 4; }
        internal Dictionary<uint, MessageHeader> _previousWriteMessageHeader = new Dictionary<uint, MessageHeader>();
        internal Dictionary<uint, MessageHeader> _previousReadMessageHeader = new Dictionary<uint, MessageHeader>();
        internal Dictionary<uint, MessageReadingState> _incompleteMessageState = new Dictionary<uint, MessageReadingState>();
        internal uint? ReadWindowAcknowledgementSize { get; set; } = null;
        internal uint? WriteWindowAcknowledgementSize { get; set; } = null;
        internal uint ReadWindowSize { get; set; } = 0;
        internal uint WriteWindowSize { get; set; } = 0;
        internal int ReadChunkSize { get; set; } = 128;
        internal bool BandwidthLimited { get; set; } = false;
        internal int _writeChunkSize = 128;
        internal readonly int EXTENDED_TIMESTAMP_LENGTH = 4;
        internal readonly int TYPE0_SIZE = 11;
        internal readonly int TYPE1_SIZE = 7;
        internal readonly int TYPE2_SIZE = 3;

        internal RtmpSession _rtmpSession = null;

        internal Amf0Reader _amf0Reader = new Amf0Reader();
        internal Amf0Writer _amf0Writer = new Amf0Writer();
        internal Amf3Reader _amf3Reader = new Amf3Reader();
        internal Amf3Writer _amf3Writer = new Amf3Writer();

        private IOPipeLine _ioPipeline = null;

        public ChunkStreamContext(IOPipeLine stream)
        {
            _rtmpSession = new RtmpSession(stream);
            _ioPipeline = stream;
            _ioPipeline.NextProcessState = ProcessState.FirstByteBasicHeader;
            _ioPipeline._bufferProcessors.Add(ProcessState.ChunkMessageHeader, ProcessChunkMessageHeader);
            _ioPipeline._bufferProcessors.Add(ProcessState.CompleteMessage, ProcessCompleteMessage);
            _ioPipeline._bufferProcessors.Add(ProcessState.ExtendedTimestamp, ProcessExtendedTimestamp);
            _ioPipeline._bufferProcessors.Add(ProcessState.FirstByteBasicHeader, ProcessFirstByteBasicHeader);
        }

        public void Dispose()
        {
            ((IDisposable)_rtmpSession).Dispose();
        }

        internal Task MultiplexMessageAsync(uint chunkStreamId, Message message)
        {
            if (!message.MessageHeader.MessageStreamId.HasValue)
            {
                throw new InvalidOperationException("cannot send message that has not attached to a message stream");
            }
            var ret = new TaskCompletionSource<int>();
            byte[] buffer = null;
            uint length = 0;
            using (var writeBuffer = new ByteBuffer())
            {
                var context = new Serialization.SerializationContext()
                {
                    Amf0Reader = _amf0Reader,
                    Amf0Writer = _amf0Writer,
                    Amf3Reader = _amf3Reader,
                    Amf3Writer = _amf3Writer,
                    WriteBuffer = writeBuffer
                };
                message.Serialize(context);
                length = (uint)writeBuffer.Length;
                //buffer = new byte[(int)length];
                buffer = new byte[(int)length];
                writeBuffer.TakeOutMemory(buffer);
            }

            try
            {
                message.MessageHeader.MessageLength = length;
                if (message.MessageHeader.MessageType == 0)
                {
                    message.MessageHeader.MessageType = message.GetType().GetCustomAttribute<RtmpMessageAttribute>().MessageTypes.First();
                }
                
                // chunking
                for (int i = 0; i < message.MessageHeader.MessageLength;)
                {
                    _previousWriteMessageHeader.TryGetValue(chunkStreamId, out var prevHeader);
                    var chunkHeaderType = SelectChunkType(message.MessageHeader, prevHeader);
                    GenerateBasicHeader(chunkHeaderType, chunkStreamId, out var basicHeader, out var basicHeaderLength);
                    GenerateMesesageHeader(chunkHeaderType, message.MessageHeader, prevHeader, out var messageHeader, out var messageHeaderLength);
                    _previousWriteMessageHeader[chunkStreamId] = message.MessageHeader;
                    var headerLength = basicHeaderLength + messageHeaderLength;
                    var bodySize = (int)(length - i >= _writeChunkSize ? _writeChunkSize : length - i);
                    //var chunkBuffer = _arrayPool.Rent(headerLength + bodySize);
                    var chunkBuffer = new byte[headerLength + bodySize];
                    basicHeader.AsSpan(0, basicHeaderLength).CopyTo(chunkBuffer);
                    messageHeader.AsSpan(0, messageHeaderLength).CopyTo(chunkBuffer.AsSpan(basicHeaderLength));
                    ////_arrayPool.Return(basicHeader);
                    ////_arrayPool.Return(messageHeader);
                    buffer.AsSpan(i, bodySize).CopyTo(chunkBuffer.AsSpan(headerLength));
                    i += bodySize;
                    var isLastChunk = message.MessageHeader.MessageLength - i == 0;
                    TaskCompletionSource<int> tcs = null;
                    if (isLastChunk)
                    {
                        tcs = ret;
                    }
                    _ioPipeline._writerQueue.Enqueue(new WriteState()
                    {
                        Buffer = chunkBuffer,
                        Length = headerLength + bodySize,
                        TaskSource = tcs
                    });
                    _ioPipeline._writerSignal.Release();
                }
            }
            finally
            {
                ////_arrayPool.Return(buffer);
            }

            return ret.Task;
        }

        private void GenerateMesesageHeader(ChunkHeaderType chunkHeaderType, MessageHeader header, MessageHeader prevHeader, out byte[] buffer, out int length)
        {
            var timestamp = header.Timestamp;
            switch (chunkHeaderType)
            {
                case ChunkHeaderType.Type0:
                    //buffer = _arrayPool.Rent(TYPE0_SIZE + EXTENDED_TIMESTAMP_LENGTH);
                    buffer = new byte[TYPE0_SIZE + EXTENDED_TIMESTAMP_LENGTH];
                    NetworkBitConverter.TryGetUInt24Bytes(timestamp >= 0xFFFFFF ? 0xFFFFFF : timestamp, buffer.AsSpan(0, 3));
                    NetworkBitConverter.TryGetUInt24Bytes(header.MessageLength, buffer.AsSpan(3, 3));
                    NetworkBitConverter.TryGetBytes((byte)header.MessageType, buffer.AsSpan(6, 1));
                    NetworkBitConverter.TryGetBytes(header.MessageStreamId.Value, buffer.AsSpan(7, 4), true);
                    length = TYPE0_SIZE;
                    break;
                case ChunkHeaderType.Type1:
                    //buffer = _arrayPool.Rent(TYPE1_SIZE + EXTENDED_TIMESTAMP_LENGTH);
                    buffer = new byte[TYPE1_SIZE + EXTENDED_TIMESTAMP_LENGTH];
                    timestamp = prevHeader.Timestamp - timestamp;
                    NetworkBitConverter.TryGetUInt24Bytes(timestamp >= 0xFFFFFF ? 0xFFFFFF : timestamp, buffer.AsSpan(0, 3));
                    NetworkBitConverter.TryGetUInt24Bytes(header.MessageLength, buffer.AsSpan(3, 3));
                    NetworkBitConverter.TryGetBytes((byte)header.MessageType, buffer.AsSpan(6, 1));
                    length = TYPE1_SIZE;
                    break;
                case ChunkHeaderType.Type2:
                    //buffer = _arrayPool.Rent(TYPE2_SIZE + EXTENDED_TIMESTAMP_LENGTH);
                    buffer = new byte[TYPE2_SIZE + EXTENDED_TIMESTAMP_LENGTH];
                    timestamp = prevHeader.Timestamp - timestamp;
                    NetworkBitConverter.TryGetUInt24Bytes(timestamp >= 0xFFFFFF ? 0xFFFFFF : timestamp, buffer.AsSpan(0, 3));
                    length = TYPE2_SIZE;
                    break;
                case ChunkHeaderType.Type3:
                    //buffer = _arrayPool.Rent(EXTENDED_TIMESTAMP_LENGTH);
                    buffer = new byte[EXTENDED_TIMESTAMP_LENGTH];
                    length = 0;
                    break;
                default:
                    throw new ArgumentException();
            }
            if (header.Timestamp == 0xFFFFFF)
            {
                NetworkBitConverter.TryGetBytes(timestamp, buffer.AsSpan(length, EXTENDED_TIMESTAMP_LENGTH));
                length += EXTENDED_TIMESTAMP_LENGTH;
            }
        }

        private void GenerateBasicHeader(ChunkHeaderType chunkHeaderType, uint chunkStreamId, out byte[] buffer, out int length)
        {
            byte fmt = (byte)chunkHeaderType;
            if (chunkStreamId >= 2 && chunkStreamId <= 63)
            {
                //buffer = new byte[1];
                buffer = new byte[1];
                buffer[0] = (byte)((byte)(fmt << 6) | chunkStreamId);
                length = 1;
            }
            else if (chunkStreamId >= 64 && chunkStreamId <= 319)
            {
                //buffer = new byte[2];
                buffer = new byte[2];
                buffer[0] = (byte)(fmt << 6);
                buffer[1] = (byte)(chunkStreamId - 64);
                length = 2;
            }
            else if (chunkStreamId >= 320 && chunkStreamId <= 65599)
            {
                //buffer = new byte[3];
                buffer = new byte[3];
                buffer[0] = (byte)((fmt << 6) | 1);
                buffer[1] = (byte)((chunkStreamId - 64) & 0xff);
                buffer[2] = (byte)((chunkStreamId - 64) >> 8);
                length = 3;
            }
            else
            {
                throw new NotSupportedException();
            }
        }

        private ChunkHeaderType SelectChunkType(MessageHeader messageHeader, MessageHeader prevHeader)
        {
            if (prevHeader == null)
            {
                return ChunkHeaderType.Type0;
            }

            if (messageHeader.Timestamp == prevHeader.Timestamp &&
                messageHeader.MessageType == prevHeader.MessageType &&
                messageHeader.MessageLength == prevHeader.MessageLength &&
                messageHeader.MessageStreamId == prevHeader.MessageStreamId)
            {
                return ChunkHeaderType.Type3;
            }
            else if (messageHeader.MessageType == prevHeader.MessageType &&
                messageHeader.MessageLength == prevHeader.MessageLength &&
                messageHeader.MessageStreamId == prevHeader.MessageStreamId)
            {
                return ChunkHeaderType.Type2;
            }
            else if (messageHeader.MessageStreamId == prevHeader.MessageStreamId)
            {
                return ChunkHeaderType.Type1;
            }
            else
            {
                return ChunkHeaderType.Type0;
            }
        }
        private void FillHeader(ChunkHeader header)
        {
            if (!_previousReadMessageHeader.TryGetValue(header.ChunkBasicHeader.ChunkStreamId, out var prevHeader) &&
                header.ChunkBasicHeader.RtmpChunkHeaderType != ChunkHeaderType.Type0)
            {
                throw new InvalidOperationException();
            }

            switch (header.ChunkBasicHeader.RtmpChunkHeaderType)
            {
                case ChunkHeaderType.Type1:
                    header.MessageHeader.MessageStreamId = prevHeader.MessageStreamId;
                    break;
                case ChunkHeaderType.Type2:
                    header.MessageHeader.MessageLength = prevHeader.MessageLength;
                    header.MessageHeader.MessageType = prevHeader.MessageType;
                    header.MessageHeader.MessageStreamId = prevHeader.MessageStreamId;
                    break;
                case ChunkHeaderType.Type3:
                    header.MessageHeader.Timestamp = prevHeader.Timestamp;
                    header.MessageHeader.MessageLength = prevHeader.MessageLength;
                    header.MessageHeader.MessageType = prevHeader.MessageType;
                    header.MessageHeader.MessageStreamId = prevHeader.MessageStreamId;
                    break;
            }
        }


        public async Task ProcessFirstByteBasicHeader(ByteBuffer buffer, CancellationToken ct)
        {
            var header = new ChunkHeader()
            {
                ChunkBasicHeader = new ChunkBasicHeader(),
                MessageHeader = new MessageHeader()
            };
            _processingChunk = header;
            //var arr = new byte[1];
            var arr = new byte[1];
            await buffer.TakeOutMemoryAsync(arr.AsMemory(0, 1));
            var basicHeader = arr[0];
            ////_arrayPool.Return(arr);
            header.ChunkBasicHeader.RtmpChunkHeaderType = (ChunkHeaderType)(basicHeader >> 6);
            header.ChunkBasicHeader.ChunkStreamId = (uint)basicHeader & 0x3F;
            if (header.ChunkBasicHeader.ChunkStreamId > 4)
            {
                throw new InvalidOperationException();
            }
            if (header.ChunkBasicHeader.ChunkStreamId != 0 && header.ChunkBasicHeader.ChunkStreamId != 0x3F)
            {
                if (header.ChunkBasicHeader.RtmpChunkHeaderType == ChunkHeaderType.Type3)
                {
                    FillHeader(header);
                    _ioPipeline.NextProcessState = ProcessState.CompleteMessage;
                }
            }
            _ioPipeline.NextProcessState = ProcessState.ChunkMessageHeader;
        }

        private async Task ProcessChunkMessageHeader(ByteBuffer buffer, CancellationToken ct)
        {
            int bytesNeed = 0;
            switch (_processingChunk.ChunkBasicHeader.ChunkStreamId)
            {
                case 0:
                    bytesNeed = 1;
                    break;
                case 0x3F:
                    bytesNeed = 2;
                    break;
            }
            switch (_processingChunk.ChunkBasicHeader.RtmpChunkHeaderType)
            {
                case ChunkHeaderType.Type0:
                    bytesNeed += TYPE0_SIZE;
                    break;
                case ChunkHeaderType.Type1:
                    bytesNeed += TYPE1_SIZE;
                    break;
                case ChunkHeaderType.Type2:
                    bytesNeed += TYPE2_SIZE;
                    break;
            }

            byte[] arr = null;
            if (_processingChunk.ChunkBasicHeader.ChunkStreamId == 0)
            {
                //arr = new byte[1];
                arr = new byte[1];
                await buffer.TakeOutMemoryAsync(arr.AsMemory(0, 1), ct);
                _processingChunk.ChunkBasicHeader.ChunkStreamId = (uint)arr[0] + 64;
                ////_arrayPool.Return(arr);
            }
            else if (_processingChunk.ChunkBasicHeader.ChunkStreamId == 0x3F)
            {
                //arr = new byte[2];
                arr = new byte[2];
                await buffer.TakeOutMemoryAsync(arr.AsMemory(0, 2), ct);
                _processingChunk.ChunkBasicHeader.ChunkStreamId = (uint)arr[1] * 256 + arr[0] + 64;
                ////_arrayPool.Return(arr);
            }
            var header = _processingChunk;
            switch (header.ChunkBasicHeader.RtmpChunkHeaderType)
            {
                case ChunkHeaderType.Type0:
                    //arr = _arrayPool.Rent(TYPE0_SIZE);
                    arr = new byte[TYPE0_SIZE];
                    await buffer.TakeOutMemoryAsync(arr.AsMemory(0, TYPE0_SIZE), ct);
                    header.MessageHeader.Timestamp = NetworkBitConverter.ToUInt24(arr.AsSpan(0, 3));
                    header.MessageHeader.MessageLength = NetworkBitConverter.ToUInt24(arr.AsSpan(3, 3));
                    header.MessageHeader.MessageType = (MessageType)arr[6];
                    header.MessageHeader.MessageStreamId = NetworkBitConverter.ToUInt32(arr.AsSpan(7, 4), true);
                    break;
                case ChunkHeaderType.Type1:
                    //arr = _arrayPool.Rent(TYPE1_SIZE);
                    arr = new byte[TYPE1_SIZE];
                    await buffer.TakeOutMemoryAsync(arr.AsMemory(0, TYPE1_SIZE), ct);
                    header.MessageHeader.Timestamp = NetworkBitConverter.ToUInt24(arr.AsSpan(0, 3));
                    header.MessageHeader.MessageLength = NetworkBitConverter.ToUInt24(arr.AsSpan(3, 3));
                    header.MessageHeader.MessageType = (MessageType)arr[6];
                    break;
                case ChunkHeaderType.Type2:
                    //arr = _arrayPool.Rent(TYPE2_SIZE);
                    arr = new byte[TYPE2_SIZE];
                    await buffer.TakeOutMemoryAsync(arr.AsMemory(0, TYPE2_SIZE), ct);
                    header.MessageHeader.Timestamp = NetworkBitConverter.ToUInt24(arr.AsSpan(0, 3));
                    break;
            }
            if (arr != null)
            {
                ////_arrayPool.Return(arr);
            }
            FillHeader(header);
            if (header.MessageHeader.Timestamp == 0x00FFFFFF)
            {
                _ioPipeline.NextProcessState = ProcessState.ExtendedTimestamp;
            }
            else
            {
                _ioPipeline.NextProcessState = ProcessState.CompleteMessage;
            }
        }

        public async Task ProcessExtendedTimestamp(ByteBuffer buffer, CancellationToken ct)
        {
            //var arr = new byte[4];
            var arr = new byte[4];
            await buffer.TakeOutMemoryAsync(arr.AsMemory(0, 4), ct);
            var extendedTimestamp = NetworkBitConverter.ToUInt32(arr.AsSpan(0, 4));
            _processingChunk.ExtendedTimestamp = extendedTimestamp;
            _processingChunk.MessageHeader.Timestamp = extendedTimestamp;
            _ioPipeline.NextProcessState = ProcessState.CompleteMessage;
        }

        public async Task ProcessCompleteMessage(ByteBuffer buffer, CancellationToken ct)
        {
            var header = _processingChunk;
            if (!_incompleteMessageState.TryGetValue(header.ChunkBasicHeader.ChunkStreamId, out var state))
            {
                state = new MessageReadingState()
                {
                    CurrentIndex = 0,
                    MessageLength = header.MessageHeader.MessageLength,
                    //Body = new byte[(int]header.MessageHeader.MessageLength)
                    Body = new byte[(int)header.MessageHeader.MessageLength]
                };
                _incompleteMessageState.Add(header.ChunkBasicHeader.ChunkStreamId, state);
            }

            var bytesNeed = (int)(state.RemainBytes >= ReadChunkSize ? ReadChunkSize : state.RemainBytes);

            if (_previousReadMessageHeader.TryGetValue(header.ChunkBasicHeader.ChunkStreamId, out var prevHeader))
            {
                if (prevHeader.MessageStreamId != header.MessageHeader.MessageStreamId)
                {
                    // inform user previous message will never be received
                    prevHeader = null;
                }
            }
            _previousReadMessageHeader[_processingChunk.ChunkBasicHeader.ChunkStreamId] = _processingChunk.MessageHeader;
            _processingChunk = null;

            await buffer.TakeOutMemoryAsync(state.Body.AsMemory(state.CurrentIndex, bytesNeed));
            state.CurrentIndex = state.CurrentIndex + bytesNeed;

            if (state.IsCompleted)
            {
                _incompleteMessageState.Remove(header.ChunkBasicHeader.ChunkStreamId);
                try
                {
                    var context = new Serialization.SerializationContext()
                    {
                        Amf0Reader = _amf0Reader,
                        Amf0Writer = _amf0Writer,
                        Amf3Reader = _amf3Reader,
                        Amf3Writer = _amf3Writer,
                        ReadBuffer = state.Body.AsMemory(0, (int)state.MessageLength)
                    };
                    if (header.MessageHeader.MessageType == MessageType.AggregateMessage)
                    {
                        var agg = new AggregateMessage()
                        {
                            MessageHeader = header.MessageHeader
                        };
                        agg.Deserialize(context);
                        foreach (var message in agg.Messages)
                        {
                            if (!_ioPipeline._options._messageFactories.TryGetValue(message.Header.MessageType, out var factory))
                            {
                                continue;
                            }
                            var msgContext = new Serialization.SerializationContext()
                            {
                                Amf0Reader = context.Amf0Reader,
                                Amf3Reader = context.Amf3Reader,
                                Amf0Writer = context.Amf0Writer,
                                Amf3Writer = context.Amf3Writer,
                                ReadBuffer = context.ReadBuffer.Slice(message.DataOffset, (int)message.DataLength)
                            };
                            try
                            {
                                var msg = factory(header.MessageHeader, msgContext, out var factoryConsumed);
                                msg.MessageHeader = header.MessageHeader;
                                msg.Deserialize(msgContext);
                                context.Amf0Reader.ResetReference();
                                context.Amf3Reader.ResetReference();
                                _rtmpSession.MessageArrived(msg);
                            }
                            catch (NotSupportedException)
                            {

                            }
                        }
                    }
                    else
                    {
                        if (_ioPipeline._options._messageFactories.TryGetValue(header.MessageHeader.MessageType, out var factory))
                        {
                            try
                            {
                                var message = factory(header.MessageHeader, context, out var factoryConsumed);
                                message.MessageHeader = header.MessageHeader;
                                context.ReadBuffer = context.ReadBuffer.Slice(factoryConsumed);
                                message.Deserialize(context);
                                context.Amf0Reader.ResetReference();
                                context.Amf3Reader.ResetReference();
                                _rtmpSession.MessageArrived(message);
                            }
                            catch (NotSupportedException)
                            {

                            }
                        }
                    }
                }
                finally
                {
                    ////_arrayPool.Return(state.Body);
                }
            }
            _ioPipeline.NextProcessState = ProcessState.FirstByteBasicHeader;
        }

    }
}
