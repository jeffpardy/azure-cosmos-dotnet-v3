﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.Azure.Cosmos.CosmosElements;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;
    using Moq;

    // Collection useful for mocking requests and repartitioning (splits / merge).
    internal sealed class InMemoryCollection
    {
        private readonly PartitionKeyDefinition partitionKeyDefinition;

        private PartitionKeyHashRangeDictionary<Records> partitionedRecords;
        private Dictionary<int, PartitionKeyHashRange> partitionKeyRangeIdToHashRange;

        public InMemoryCollection(PartitionKeyDefinition partitionKeyDefinition)
        {
            PartitionKeyHashRange fullRange = new PartitionKeyHashRange(startInclusive: null, endExclusive: null);
            PartitionKeyHashRanges partitionKeyHashRanges = PartitionKeyHashRanges.Create(new PartitionKeyHashRange[] { fullRange });
            this.partitionedRecords = new PartitionKeyHashRangeDictionary<Records>(partitionKeyHashRanges);
            this.partitionKeyDefinition = partitionKeyDefinition ?? throw new ArgumentNullException(nameof(partitionKeyDefinition));
            this.partitionKeyRangeIdToHashRange = new Dictionary<int, PartitionKeyHashRange>()
            {
                { 0, fullRange }
            };
        }

        public Record CreateItem(CosmosObject payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            PartitionKeyHash partitionKeyHash = GetHashFromPayload(payload, this.partitionKeyDefinition);
            if (!this.partitionedRecords.TryGetValue(partitionKeyHash, out Records records))
            {
                records = new Records();
                this.partitionedRecords[partitionKeyHash] = records;
            }

            return records.Add(payload);
        }

        public bool TryReadItem(CosmosElement partitionKey, Guid identifier, out Record record)
        {
            PartitionKeyHash partitionKeyHash = GetHashFromPartitionKey(partitionKey, this.partitionKeyDefinition);
            if (!this.partitionedRecords.TryGetValue(partitionKeyHash, out Records records))
            {
                record = default;
                return false;
            }

            foreach (Record candidate in records)
            {
                bool identifierMatches = candidate.Identifier == identifier;

                CosmosElement candidatePartitionKey = GetPartitionKeyFromPayload(candidate.Payload, this.partitionKeyDefinition);
                bool partitionKeyMatches = CosmosElementEqualityComparer.Value.Equals(candidatePartitionKey, partitionKey);

                if (identifierMatches && partitionKeyMatches)
                {
                    record = candidate;
                    return true;
                }
            }

            record = default;
            return false;
        }

        public bool TryReadFeed(int partitionKeyRangeId, int pageIndex, int pageSize, out List<Record> page)
        {
            if (!this.partitionKeyRangeIdToHashRange.TryGetValue(partitionKeyRangeId, out PartitionKeyHashRange range))
            {
                page = default;
                return false;
            }

            if (!this.partitionedRecords.TryGetValue(range, out Records records))
            {
                throw new InvalidOperationException("failed to find the range.");
            }

            page = records.Skip(pageIndex * pageSize).Take(pageSize).ToList();
            return true;
        }

        public IReadOnlyDictionary<int, PartitionKeyHashRange> PartitionKeyRangeFeedReed() => this.partitionKeyRangeIdToHashRange;

        public void Split(int partitionKeyRangeId)
        {
            // Get the current range and records
            if (!this.partitionKeyRangeIdToHashRange.TryGetValue(partitionKeyRangeId, out PartitionKeyHashRange parentRange))
            {
                throw new InvalidOperationException("Failed to find the range.");
            }

            if (!this.partitionedRecords.TryGetValue(parentRange, out Records records))
            {
                throw new InvalidOperationException("failed to find the range.");
            }

            int maxPartitionKeyRangeId = this.partitionKeyRangeIdToHashRange.Keys.Max();

            // Split the range space
            PartitionKeyHashRanges partitionKeyHashRanges = PartitionKeyHashRangeSplitterAndMerger.SplitRange(parentRange, 2);

            // Update the partition routing map
            Dictionary<int, PartitionKeyHashRange> newPartitionKeyRangeIdToHashRange = new Dictionary<int, PartitionKeyHashRange>()
            {
                { maxPartitionKeyRangeId + 1, partitionKeyHashRanges.First() },
                { maxPartitionKeyRangeId + 2, partitionKeyHashRanges.Last() },
            };

            // Copy over the partitioned records (minus the parent range)
            PartitionKeyHashRangeDictionary<Records> newPartitionedRecords = new PartitionKeyHashRangeDictionary<Records>(
                PartitionKeyHashRanges.Create(
                    newPartitionKeyRangeIdToHashRange.Values));

            foreach (PartitionKeyHashRange range in this.partitionKeyRangeIdToHashRange.Values)
            {
                if (!range.Equals(parentRange))
                {
                    newPartitionedRecords[range] = this.partitionedRecords[range];
                }
            }

            this.partitionedRecords = newPartitionedRecords;
            this.partitionKeyRangeIdToHashRange = newPartitionKeyRangeIdToHashRange;

            // Rehash the records in the parent range
            foreach (Record record in records)
            {
                this.CreateItem(record.Payload);
            }
        }

        private static PartitionKeyHash GetHashFromPayload(CosmosObject payload, PartitionKeyDefinition partitionKeyDefinition)
        {
            CosmosElement partitionKey = GetPartitionKeyFromPayload(payload, partitionKeyDefinition);
            return GetHashFromPartitionKey(partitionKey, partitionKeyDefinition);
        }

        private static CosmosElement GetPartitionKeyFromPayload(CosmosObject payload, PartitionKeyDefinition partitionKeyDefinition)
        {
            // Restrict the partition key definition for now to keep things simple
            if (partitionKeyDefinition.Kind != PartitionKind.Hash)
            {
                throw new ArgumentOutOfRangeException("Can only support hash partitioning");
            }

            if (partitionKeyDefinition.Version != PartitionKeyDefinitionVersion.V2)
            {
                throw new ArgumentOutOfRangeException("Can only support hash v2");
            }

            if (partitionKeyDefinition.Paths.Count != 1)
            {
                throw new ArgumentOutOfRangeException("Can only support a single partition key path.");
            }

            IEnumerable<string> tokens = partitionKeyDefinition.Paths[0].Split("/").Skip(1);

            CosmosElement partitionKey = payload;
            foreach (string token in tokens)
            {
                if (partitionKey != default)
                {
                    if (!payload.TryGetValue(token, out partitionKey))
                    {
                        partitionKey = default;
                    }
                }
            }

            return partitionKey;
        }

        private static PartitionKeyHash GetHashFromPartitionKey(CosmosElement partitionKey, PartitionKeyDefinition partitionKeyDefinition)
        {
            // Restrict the partition key definition for now to keep things simple
            if (partitionKeyDefinition.Kind != PartitionKind.Hash)
            {
                throw new ArgumentOutOfRangeException("Can only support hash partitioning");
            }

            if (partitionKeyDefinition.Version != PartitionKeyDefinitionVersion.V2)
            {
                throw new ArgumentOutOfRangeException("Can only support hash v2");
            }

            if (partitionKeyDefinition.Paths.Count != 1)
            {
                throw new ArgumentOutOfRangeException("Can only support a single partition key path.");
            }

            PartitionKeyHash partitionKeyHash;
            switch (partitionKey)
            {
                case null:
                    partitionKeyHash = PartitionKeyHash.V2.HashUndefined();
                    break;

                case CosmosString stringPartitionKey:
                    partitionKeyHash = PartitionKeyHash.V2.Hash(stringPartitionKey.Value);
                    break;

                case CosmosNumber numberPartitionKey:
                    partitionKeyHash = PartitionKeyHash.V2.Hash(Number64.ToDouble(numberPartitionKey.Value));
                    break;

                case CosmosBoolean cosmosBoolean:
                    partitionKeyHash = PartitionKeyHash.V2.Hash(cosmosBoolean.Value);
                    break;

                case CosmosNull _:
                    partitionKeyHash = PartitionKeyHash.V2.HashNull();
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

            return partitionKeyHash;
        }

        public sealed class Record
        {
            private Record(long resourceIdentifier, long timestamp, Guid identifier, CosmosObject payload)
            {
                this.ResourceIdentifier = resourceIdentifier < 0 ? throw new ArgumentOutOfRangeException(nameof(resourceIdentifier)) : resourceIdentifier;
                this.Timestamp = timestamp < 0 ? throw new ArgumentOutOfRangeException(nameof(timestamp)) : timestamp;
                this.Identifier = identifier;
                this.Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            }

            public long ResourceIdentifier { get; }

            public long Timestamp { get; }

            public Guid Identifier { get; }

            public CosmosObject Payload { get; }

            public static Record Create(long previousResourceIdentifier, CosmosObject payload)
            {
                return new Record(previousResourceIdentifier + 1, DateTime.UtcNow.Ticks, Guid.NewGuid(), payload);
            }
        }

        private sealed class Records : IReadOnlyList<Record>
        {
            private readonly List<Record> storage;

            public Records()
            {
                this.storage = new List<Record>();
            }

            public Record this[int index] => this.storage[index];

            public int Count => this.storage.Count;

            public IEnumerator<Record> GetEnumerator() => this.storage.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator() => this.storage.GetEnumerator();

            public Record Add(CosmosObject payload)
            {
                long previousResourceId;
                if (this.Count == 0)
                {
                    previousResourceId = 0;
                }
                else
                {
                    previousResourceId = this.storage[this.storage.Count - 1].ResourceIdentifier;
                }

                Record record = Record.Create(previousResourceId, payload);
                this.storage.Add(record);
                return record;
            }
        }
    }
}