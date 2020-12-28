﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LaunchDarkly.Logging;
using LaunchDarkly.Sdk.Server.Interfaces;
using Xunit;

using static LaunchDarkly.Sdk.Server.Interfaces.DataStoreTypes;

namespace LaunchDarkly.Sdk.Server.SharedTests.DataStore
{
    /// <summary>
    /// A configurable test suite for all implementations of <c>IPersistentDataStore</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each implementation of those interfaces should define a test class that is a subclass of this
    /// class for their implementation type, and run it in the unit tests for their project.
    /// </para>
    /// <para>
    /// In order to be testable with this class, a data store implementation must have the following
    /// characteristics:
    /// </para>
    /// <list type="number">
    /// <item>It has some notion of a "prefix" string that can be used to distinguish between different
    /// SDK instances using the same underlying database.</item>
    /// <item>Two instances of the same data store type with the same configuration, and the same prefix,
    /// should be able to see each other's data.</item>
    /// </list>
    /// <para>
    /// You must override the <see cref="Configuration"/> property to provide details specific to
    /// your implementation type.
    /// </para>
    /// </remarks>
    public abstract class PersistentDataStoreBaseTests<StoreT> where StoreT : IDisposable
    {
        /// <summary>
        /// Override this method to create the configuration for the test suite.
        /// </summary>
        protected abstract PersistentDataStoreTestConfig<StoreT> Configuration { get; }

        private readonly TestEntity item1 = new TestEntity("first", 5, "value1");
        private readonly TestEntity item2 = new TestEntity("second", 5, "value2");
        private readonly TestEntity other1 = new TestEntity("third", 5, "othervalue1");
        private readonly string unusedKey = "whatever";

        [Fact]
        public async void StoreNotInitializedBeforeInit()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                Assert.False(await Initialized(store));
            }
        }

        [Fact]
        public async void OneInstanceCanDetectIfAnotherInstanceHasInitializedStore()
        {
            await ClearAllData();
            using (var store1 = CreateStoreImpl())
            {
                await Init(store1, new DataBuilder().Add(TestEntity.Kind, item1).BuildSerialized());

                using (var store2 = CreateStoreImpl())
                {
                    Assert.True(await Initialized(store2));
                }
            }
        }

        [Fact]
        public async void StoreInitializedAfterInit()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().BuildSerialized());
                Assert.True(await Initialized(store));
            }
        }

        [Fact]
        public async void InitCompletelyReplacesExistingData()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                var allData = new DataBuilder()
                     .Add(TestEntity.Kind, item1, item2)
                     .Add(TestEntity.OtherKind, other1)
                     .BuildSerialized();
                await Init(store, allData);

                var item2v2 = item2.NextVersion();
                var data2 = new DataBuilder()
                    .Add(TestEntity.Kind, item2v2)
                    .Add(TestEntity.OtherKind)
                    .BuildSerialized();
                await Init(store, data2);

                Assert.Null(await Get(store, TestEntity.Kind, item1.Key));
                AssertEqualsSerializedItem(item2v2, await Get(store, TestEntity.Kind, item2.Key));
                Assert.Null(await Get(store, TestEntity.OtherKind, other1.Key));
            }
        }

        [Fact]
        public async void GetExistingItem()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                AssertEqualsSerializedItem(item1, await Get(store, TestEntity.Kind, item1.Key));
            }
        }

        [Fact]
        public async void GetNonexistingItem()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                Assert.Null(await Get(store, TestEntity.Kind, unusedKey));
            }
        }

        [Fact]
        public async void GetAllItems()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2)
                .Add(TestEntity.OtherKind, other1).BuildSerialized());
                var result = await GetAll(store, TestEntity.Kind);
                AssertSerializedItemsCollection(result, item1, item2);
            }
        }

        [Fact]
        public async void GetAllWithDeletedItem()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                var deletedItem = new TestEntity(unusedKey, 1, null);
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2, deletedItem)
                    .Add(TestEntity.OtherKind, other1).BuildSerialized());
                var result = await GetAll(store, TestEntity.Kind);
                AssertSerializedItemsCollection(result, item1, item2, deletedItem);
            }
        }

        [Fact]
        public async void UpsertWithNewerVersion()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                var newer = item1.NextVersion();
                await Upsert(store, TestEntity.Kind, item1.Key, newer.SerializedItemDescriptor);
                AssertEqualsSerializedItem(newer, await Get(store, TestEntity.Kind, item1.Key));
            }
        }

        [Fact]
        public async void UpsertWithSameVersion()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                var sameVersionDifferentValue = item1.WithValue("modified");
                await Upsert(store, TestEntity.Kind, item1.Key, sameVersionDifferentValue.SerializedItemDescriptor);
                AssertEqualsSerializedItem(item1, await Get(store, TestEntity.Kind, item1.Key));
            }
        }

        [Fact]
        public async void UpsertWithOlderVersion()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                var older = item1.WithVersion(item1.Version - 1);
                await Upsert(store, TestEntity.Kind, item1.Key, older.SerializedItemDescriptor);
                AssertEqualsSerializedItem(item1, await Get(store, TestEntity.Kind, item1.Key));
            }
        }

        [Fact]
        public async void UpsertNewItem()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                var newItem = new TestEntity(unusedKey, 1, "newvalue");
                await Upsert(store, TestEntity.Kind, unusedKey, newItem.SerializedItemDescriptor);
                AssertEqualsSerializedItem(newItem, await Get(store, TestEntity.Kind, newItem.Key));
            }
        }

        [Fact]
        public async void DeleteWithNewerVersion()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                var deletedItem = new TestEntity(item1.Key, item1.Version + 1, null);
                await Upsert(store, TestEntity.Kind, item1.Key, deletedItem.SerializedItemDescriptor);
                AssertEqualsSerializedItem(deletedItem, await Get(store, TestEntity.Kind, item1.Key));
            }
        }

        [Fact]
        public async void DeleteWithSameVersion()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                var deletedItem = new TestEntity(item1.Key, item1.Version, null);
                await Upsert(store, TestEntity.Kind, item1.Key, deletedItem.SerializedItemDescriptor);
                AssertEqualsSerializedItem(item1, await Get(store, TestEntity.Kind, item1.Key));
            }
        }

        [Fact]
        public async void DeleteWithOlderVersion()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1, item2).BuildSerialized());
                var deletedItem = new TestEntity(item1.Key, item1.Version - 1, null);
                await Upsert(store, TestEntity.Kind, item1.Key, deletedItem.SerializedItemDescriptor);
                AssertEqualsSerializedItem(item1, await Get(store, TestEntity.Kind, item1.Key));
            }
        }

        [Fact]
        public async void DeleteUnknownItem()
        {
            await ClearAllData();
            using (var store = CreateStoreImpl())
            {
                await Init(store, new DataBuilder().Add(TestEntity.Kind, item1).BuildSerialized());
                var deletedItem = new TestEntity(unusedKey, 99, null);
                await Upsert(store, TestEntity.Kind, unusedKey, deletedItem.SerializedItemDescriptor);
                AssertEqualsSerializedItem(deletedItem, await Get(store, TestEntity.Kind, unusedKey));
            }
        }

        [Fact]
        public async void StoresWithDifferentPrefixAreIndependent()
        {
            // The prefix parameter, if supported, is a namespace for all of a store's data,
            // so that it won't interfere with data from some other instance with a different
            // prefix. This test verifies that Init, Get, All, and Upsert are all respecting
            // the prefix.
            await ClearAllData();
            using (var store1 = CreateStoreImpl("aaa"))
            {
                using (var store2 = CreateStoreImpl("bbb"))
                {
                    Assert.False(await Initialized(store1));
                    Assert.False(await Initialized(store2));

                    var store1Item1 = new TestEntity("a", 1, "1a");
                    var store1Item2 = new TestEntity("b", 1, "1b");
                    var store1Item3 = new TestEntity("c", 1, "1c");
                    var store2Item1 = new TestEntity("a", 99, "2a");
                    var store2Item2 = new TestEntity("bb", 1, "2b"); // skipping key "b" validates that store2.Init doesn't delete store1's "b" key
                    var store2Item3 = new TestEntity("c", 2, "2c");
                    await Init(store1, new DataBuilder().Add(TestEntity.Kind, store1Item1, store1Item2).BuildSerialized());
                    await Init(store2, new DataBuilder().Add(TestEntity.Kind, store2Item1, store2Item2).BuildSerialized());
                    await Upsert(store1, TestEntity.Kind, store1Item3.Key, store1Item3.SerializedItemDescriptor);
                    await Upsert(store2, TestEntity.Kind, store2Item3.Key, store2Item3.SerializedItemDescriptor);

                    var items1 = await GetAll(store1, TestEntity.Kind);
                    AssertSerializedItemsCollection(items1, store1Item1, store1Item2, store1Item3);
                    var items2 = await GetAll(store2, TestEntity.Kind);
                    AssertSerializedItemsCollection(items2, store2Item1, store2Item2, store2Item3);
                }
            }
        }

        [Fact]
        public async void UpsertRaceConditionAgainstOtherClientWithLowerVersion()
        {
            if (Configuration.SetConcurrentModificationHookAction is null)
            {
                return;
            }
            var key = "key";
            var item1 = new TestEntity(key, 1, "value1");
            using (var store2 = CreateStoreImpl())
            {
                var action = MakeConcurrentModifier(store2, key, 2, 3, 4);
                var store1 = CreateStoreImplWithUpdateHook(action);
                await Init(store1, new DataBuilder().Add(TestEntity.Kind, item1).BuildSerialized());

                var item10 = item1.WithVersion(10);
                await Upsert(store1, TestEntity.Kind, item1.Key, item10.SerializedItemDescriptor);

                AssertEqualsSerializedItem(item10, await Get(store1, TestEntity.Kind, key));
            }
        }
        
        [Fact]
        public async void UpsertRaceConditionAgainstOtherClientWithHigherVersion()
        {
            if (Configuration.SetConcurrentModificationHookAction is null)
            {
                return;
            }
            var key = "key";
            var item1 = new TestEntity(key, 1, "value1");
            using (var store2 = CreateStoreImpl())
            {
                var action = MakeConcurrentModifier(store2, key, 3, 4, 5);
                var store1 = CreateStoreImplWithUpdateHook(action);
                await Init(store1, new DataBuilder().Add(TestEntity.Kind, item1).BuildSerialized());

                var item2 = item1.WithVersion(2);
                await Upsert(store1, TestEntity.Kind, item1.Key, item2.SerializedItemDescriptor);

                var item5 = item1.WithVersion(5);
                AssertEqualsSerializedItem(item5, await Get(store1, TestEntity.Kind, key));
            }
        }

        private Action MakeConcurrentModifier(StoreT store, string key, params int[] versionsToWrite)
        {
            var i = 0;
            return () =>
            {
                if (i < versionsToWrite.Length)
                {
                    var e = new TestEntity(key, versionsToWrite[i], "value" + versionsToWrite[i]);
                    AsyncUtils.WaitSafely(() => Upsert(store, TestEntity.Kind, key, e.SerializedItemDescriptor));
                    i++;
                }
            };
        }

        private StoreT CreateStoreImpl(string prefix = null)
        {
            var context = new LdClientContext(new BasicConfiguration("sdk-key", false, Logs.None.Logger("")),
                LaunchDarkly.Sdk.Server.Configuration.Default("sdk-key"));
            if (Configuration.StoreFactoryFunc != null)
            {
                if (!typeof(IPersistentDataStore).IsAssignableFrom(typeof(StoreT)))
                {
                    throw new InvalidOperationException("StoreFactoryFunc was set, but store type does not implement IPersistentDataStore");
                }
                return (StoreT)Configuration.StoreFactoryFunc(prefix).CreatePersistentDataStore(context);
            }
            if (Configuration.StoreAsyncFactoryFunc != null)
            {
                if (!typeof(IPersistentDataStoreAsync).IsAssignableFrom(typeof(StoreT)))
                {
                    throw new InvalidOperationException("StoreAsyncFactoryFunc was set, but store type does not implement IPersistentDataStoreAsync");
                }
                return (StoreT)Configuration.StoreAsyncFactoryFunc(prefix).CreatePersistentDataStore(context);
            }
            throw new InvalidOperationException("neither StoreFactoryFunc nor StoreAsyncFactoryFunc was set");
        }

        private StoreT CreateStoreImplWithUpdateHook(Action hook)
        {
            var store = CreateStoreImpl();
            Configuration.SetConcurrentModificationHookAction(store, hook);
            return store;
        }

        private Task ClearAllData(string prefix = null)
        {
            if (Configuration.ClearDataAction is null)
            {
                throw new InvalidOperationException("configuration did not specify ClearDataAction");
            }
            return Configuration.ClearDataAction(prefix);
        }

        private static async Task<bool> Initialized(StoreT store)
        {
            if (store is IPersistentDataStore syncStore)
            {
                return syncStore.Initialized();
            }
            return await (store as IPersistentDataStoreAsync).InitializedAsync();
        }

        private static async Task Init(StoreT store, FullDataSet<SerializedItemDescriptor> allData)
        {
            if (store is IPersistentDataStore syncStore)
            {
                syncStore.Init(allData);
            }
            else
            {
                await (store as IPersistentDataStoreAsync).InitAsync(allData);
            }
        }

        private static async Task<SerializedItemDescriptor?> Get(StoreT store, DataKind kind, string key)
        {
            if (store is IPersistentDataStore syncStore)
            {
                return syncStore.Get(kind, key);
            }
            return await (store as IPersistentDataStoreAsync).GetAsync(kind, key);
        }

        private static async Task<KeyedItems<SerializedItemDescriptor>> GetAll(StoreT store, DataKind kind)
        {
            if (store is IPersistentDataStore syncStore)
            {
                return syncStore.GetAll(kind);
            }
            return await (store as IPersistentDataStoreAsync).GetAllAsync(kind);
        }

        private static async Task<bool> Upsert(StoreT store, DataKind kind, string key, SerializedItemDescriptor item)
        {
            if (store is IPersistentDataStore syncStore)
            {
                return syncStore.Upsert(kind, key, item);
            }
            return await (store as IPersistentDataStoreAsync).UpsertAsync(kind, key, item);
        }

        private static void AssertEqualsSerializedItem(TestEntity item, SerializedItemDescriptor? serializedItemDesc)
        {
            // This allows for the fact that a PersistentDataStore may not be able to get the item version without
            // deserializing it, so we allow the version to be zero.
            Assert.NotNull(serializedItemDesc);
            Assert.Equal(item.SerializedItemDescriptor.SerializedItem, serializedItemDesc.Value.SerializedItem);
            if (serializedItemDesc.Value.Version != 0)
            {
                Assert.Equal(item.Version, serializedItemDesc.Value.Version);
            }
        }

        private static void AssertSerializedItemsCollection(KeyedItems<SerializedItemDescriptor> serializedItems, params TestEntity[] expectedItems)
        {
            var sortedItems = serializedItems.Items.OrderBy(kv => kv.Key);
            Assert.Collection(sortedItems,
                expectedItems.Select<TestEntity, Action<KeyValuePair<string, SerializedItemDescriptor>>>(item =>
                    kv =>
                    {
                        Assert.Equal(item.Key, kv.Key);
                        AssertEqualsSerializedItem(item, kv.Value);
                    }
                ).ToArray()
                );
        }
    }
}
