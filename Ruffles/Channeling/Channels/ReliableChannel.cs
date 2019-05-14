﻿using System;
using System.Collections.Generic;
using Ruffles.Configuration;
using Ruffles.Connections;
using Ruffles.Memory;
using Ruffles.Messaging;

namespace Ruffles.Channeling.Channels
{
    internal class ReliableChannel : IChannel
    {
        private struct PendingOutgoingPacket : IMemoryReleasable
        {
            public bool IsAlloced => Memory != null && !Memory.isDead;

            public bool Alive;
            public ushort Sequence;
            public HeapMemory Memory;
            public DateTime LastSent;
            public DateTime FirstSent;
            public ushort Attempts;

            public void DeAlloc()
            {
                MemoryManager.DeAlloc(Memory);
            }
        }

        // Incoming sequencing
        private readonly HashSet<ushort> _incomingAckedPackets = new HashSet<ushort>();
        private ushort _incomingLowestAckedSequence;

        // Outgoing sequencing
        private ushort _lastOutboundSequenceNumber;
        private readonly MessageSequencer<PendingOutgoingPacket> _sendSequencer;

        // Channel info
        private readonly byte channelId;
        private readonly Connection connection;
        private readonly ListenerConfig config;

        internal ReliableChannel(byte channelId, Connection connection, ListenerConfig config)
        {
            this.channelId = channelId;
            this.connection = connection;
            this.config = config;

            _sendSequencer = new MessageSequencer<PendingOutgoingPacket>(config.ReliabilityWindowSize);
        }

        public HeapMemory HandlePoll()
        {
            return null;
        }

        public ArraySegment<byte>? HandleIncomingMessagePoll(ArraySegment<byte> payload, out bool hasMore)
        {
            // Reliable has one message in equal no more than one out.
            hasMore = false;

            // Read the sequence number
            ushort sequence = (ushort)(payload.Array[payload.Offset] | (ushort)(payload.Array[payload.Offset + 1] << 8));

            if (SequencingUtils.Distance(sequence, _incomingLowestAckedSequence, sizeof(ushort)) <= 0 || _incomingAckedPackets.Contains(sequence))
            {
                // We have already acked this message. Ack again

                SendAck(sequence);

                return null;
            }
            else if (sequence == _incomingLowestAckedSequence + 1)
            {
                // This is the "next" packet

                do
                {
                    // Remove previous
                    _incomingAckedPackets.Remove(_incomingLowestAckedSequence);

                    _incomingLowestAckedSequence++;
                }
                while (_incomingAckedPackets.Contains((ushort)(_incomingLowestAckedSequence + 1)));

                // Ack the new message
                SendAck(sequence);

                return new ArraySegment<byte>(payload.Array, payload.Offset + 2, payload.Count - 2);
            }
            else if (SequencingUtils.Distance(sequence, _incomingLowestAckedSequence, sizeof(ushort)) > 0 && !_incomingAckedPackets.Contains(sequence))
            {
                // This is a future packet

                // Add to sequencer
                _incomingAckedPackets.Add(sequence);

                SendAck(sequence);

                return new ArraySegment<byte>(payload.Array, payload.Offset + 2, payload.Count - 2);
            }

            return null;
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

            // Add the memory to pending
            _sendSequencer[_lastOutboundSequenceNumber] = new PendingOutgoingPacket()
            {
                Alive = true,
                Sequence = _lastOutboundSequenceNumber,
                Attempts = 1,
                LastSent = DateTime.Now,
                FirstSent = DateTime.Now,
                Memory = memory
            };

            // Tell the caller NOT to dealloc the memory, the channel needs it for resend purposes.
            dealloc = false;

            return memory;
        }

        public void HandleAck(ArraySegment<byte> payload)
        {
            // Read the sequence number
            ushort sequence = (ushort)(payload.Array[payload.Offset] | (ushort)(payload.Array[payload.Offset + 1] << 8));

            if (_sendSequencer[sequence].Alive)
            {
                // Dealloc the memory held by the sequencer
                MemoryManager.DeAlloc(_sendSequencer[sequence].Memory);

                // TODO: Remove roundtripping from channeled packets and make specific ping-pong packets

                // Get the roundtrp
                ulong roundtrip = (ulong)Math.Round((DateTime.Now - _sendSequencer[sequence].FirstSent).TotalMilliseconds);

                // Report to the connection
                connection.AddRoundtripSample(roundtrip);

                // Kill the packet
                _sendSequencer[sequence] = new PendingOutgoingPacket()
                {
                    Alive = false,
                    Sequence = sequence
                };
            }

            for (ushort i = sequence; _sendSequencer[i].Alive; i++)
            {
                _incomingLowestAckedSequence = i;
            }
        }

        public void InternalUpdate()
        {
            long distance = SequencingUtils.Distance(_lastOutboundSequenceNumber, _incomingLowestAckedSequence, sizeof(ushort));

            for (ushort i = _incomingLowestAckedSequence; i < _incomingLowestAckedSequence + distance; i++)
            {
                if (_sendSequencer[i].Alive)
                {
                    if (_sendSequencer[i].Attempts > config.ReliabilityMaxResendAttempts)
                    {
                        // If they don't ack the message, disconnect them
                        connection.Disconnect(false);
                    }
                    else if ((DateTime.Now - _sendSequencer[i].LastSent).TotalMilliseconds > connection.Roundtrip + config.ReliabilityResendExtraDelay)
                    {
                        _sendSequencer[i] = new PendingOutgoingPacket()
                        {
                            Alive = true,
                            Attempts = (ushort)(_sendSequencer[i].Attempts + 1),
                            LastSent = DateTime.Now,
                            FirstSent = _sendSequencer[i].FirstSent,
                            Memory = _sendSequencer[i].Memory,
                            Sequence = i
                        };

                        connection.SendRaw(new ArraySegment<byte>(_sendSequencer[i].Memory.Buffer, _sendSequencer[i].Memory.VirtualOffset, _sendSequencer[i].Memory.VirtualCount));
                    }
                }
            }
        }

        public void Reset()
        {
            // Clear all incoming states
            _incomingAckedPackets.Clear();
            _incomingLowestAckedSequence = 0;

            // Clear all outgoing states
            _sendSequencer.Release();
            _lastOutboundSequenceNumber = 0;
        }

        private void SendAck(ushort sequence)
        {
            // Alloc ack memory
            HeapMemory ackMemory = MemoryManager.Alloc(4);

            // Write header
            ackMemory.Buffer[0] = (byte)MessageType.Ack;
            ackMemory.Buffer[1] = (byte)channelId;

            // Write sequence
            ackMemory.Buffer[2] = (byte)sequence;
            ackMemory.Buffer[3] = (byte)(sequence >> 8);

            // Send ack
            connection.SendRaw(new ArraySegment<byte>(ackMemory.Buffer, 0, 4));

            // Return memory
            MemoryManager.DeAlloc(ackMemory);
        }
    }
}