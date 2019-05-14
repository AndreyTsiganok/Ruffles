﻿using System;
using Ruffles.Configuration;
using Ruffles.Connections;
using Ruffles.Memory;
using Ruffles.Messaging;

namespace Ruffles.Channeling.Channels
{
    internal class UnreliableChannel : IChannel
    {
        // Incoming sequencing
        private readonly SlidingWindow<bool> _incomingAckedPackets;
        private ushort _incomingLowestAckedSequence;

        // Outgoing sequencing
        private ushort _lastOutboundSequenceNumber;

        // Channel info
        private readonly byte channelId;
        private readonly Connection connection;
        private readonly ListenerConfig config;

        internal UnreliableChannel(byte channelId, Connection connection, ListenerConfig config)
        {
            this.channelId = channelId;
            this.connection = connection;
            this.config = config;

            _incomingAckedPackets = new SlidingWindow<bool>(config.ReliabilityWindowSize);
        }

        public HeapMemory CreateOutgoingMessage(ArraySegment<byte> payload, out bool dealloc)
        {
            // Increment the sequence number
            _lastOutboundSequenceNumber++;

            // Allocate the memory
            HeapMemory memory = MemoryManager.Alloc(payload.Count + 4);

            // Write headers
            memory.Buffer[0] = (byte)MessageType.Data;
            memory.Buffer[1] = channelId;

            // Write the sequence
            memory.Buffer[2] = (byte)_lastOutboundSequenceNumber;
            memory.Buffer[3] = (byte)(_lastOutboundSequenceNumber >> 8);

            // Copy the payload
            Buffer.BlockCopy(payload.Array, payload.Offset, memory.Buffer, 4, payload.Count);

            // Tell the caller to deallc the memory
            dealloc = true;

            return memory;
        }

        public void HandleAck(ArraySegment<byte> payload)
        {
            // Unreliable messages have no acks.
        }

        public ArraySegment<byte>? HandleIncomingMessagePoll(ArraySegment<byte> payload, out bool hasMore)
        {
            // Unreliable has one message in equal no more than one out.
            hasMore = false;

            // Read the sequence number
            ushort sequence = (ushort)(payload.Array[payload.Offset] | (ushort)(payload.Array[payload.Offset + 1] << 8));

            if (SequencingUtils.Distance(sequence, _incomingLowestAckedSequence, sizeof(ushort)) <= 0 || _incomingAckedPackets[sequence])
            {
                // We have already received this message. Ignore it.
                return null;
            }
            else if (sequence == _incomingLowestAckedSequence + 1)
            {
                // This is the "next" packet

                do
                {
                    // Remove previous
                    _incomingAckedPackets[_incomingLowestAckedSequence] = false;

                    _incomingLowestAckedSequence++;
                }
                while (_incomingAckedPackets[(ushort)(_incomingLowestAckedSequence + 1)]);

                return new ArraySegment<byte>(payload.Array, payload.Offset + 2, payload.Count - 2);
            }
            else if (SequencingUtils.Distance(sequence, _incomingLowestAckedSequence, sizeof(ushort)) > 0 && !_incomingAckedPackets[sequence])
            {
                // This is a future packet

                // Add to sequencer
                _incomingAckedPackets[sequence] = true;

                return new ArraySegment<byte>(payload.Array, payload.Offset + 2, payload.Count - 2);
            }

            return null;
        }

        public HeapMemory HandlePoll()
        {
            return null;
        }

        public void InternalUpdate()
        {
            // Unreliable doesnt need to resend, thus no internal loop is required
        }

        public void Reset()
        {
            // Clear all incoming states
            _incomingAckedPackets.Release();
            _incomingLowestAckedSequence = 0;

            // Clear all outgoing states
            _lastOutboundSequenceNumber = 0;
        }
    }
}