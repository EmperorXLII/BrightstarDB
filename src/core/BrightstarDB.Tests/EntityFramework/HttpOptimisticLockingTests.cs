﻿#if !PORTABLE
using System;
using NUnit.Framework;

namespace BrightstarDB.Tests.EntityFramework
{
    [TestFixture]
    public class HttpOptimisticLockingTests : OptimisticLockingTestsBase
    {
        private readonly string _storeName = "HttpOptimisticLockingTests_" + DateTime.Now.Ticks;

        [TestFixtureSetUp]
        public void SetUp()
        {
            StartService();
        }

        [TestFixtureTearDown]
        public void TearDown()
        {
            CloseService();
        }

        #region Overrides of OptimisticLockingTestsBase

        protected override MyEntityContext NewContext()
        {
            return new MyEntityContext(
                String.Format("type=http;endpoint=http://localhost:8090/brightstar;storeName={0};optimisticLocking=true", _storeName));
        }

        #endregion

        #region Single Object Updates
        [Test]
        public new void TestSimplePropertyRefreshWithClientWins()
        {
            base.TestSimplePropertyRefreshWithClientWins();
        }

        [Test]
        public new void TestSimplePropertyRefreshWithStoreWins()
        {
            base.TestSimplePropertyRefreshWithStoreWins();

        }

        [Test]
        public new void TestRelatedObjectRefreshWithClientWins()
        {
            base.TestRelatedObjectRefreshWithClientWins();
        }

        [Test]
        public new void TestRelatedObjectRefreshWithStoreWins()
        {
            base.TestRelatedObjectRefreshWithStoreWins();
        }

        [Test]
        public new void TestLiteralCollectionRefreshWithClientWins()
        {
            base.TestLiteralCollectionRefreshWithClientWins();
        }

        [Test]
        public new void TestLiteralCollectionRefreshWithStoreWins()
        {
            base.TestLiteralCollectionRefreshWithStoreWins();
        }

        [Test]
        public new void TestObjectCollectionRefreshWithClientWins()
        {
            base.TestObjectCollectionRefreshWithClientWins();
        }

        [Test]
        public new void TestObjectCollectionRefreshWithStoreWins()
        {
            base.TestObjectCollectionRefreshWithStoreWins();
        }

        #endregion

        #region Multiple Object Updates

        [Test]
        public new void MultiLiteralPropertyRefreshClientWins()
        {
            base.MultiLiteralPropertyRefreshClientWins();
        }

        [Test]
        public new void MultiLiteralPropertyRefreshStoreWins()
        {
            base.MultiLiteralPropertyRefreshStoreWins();
        }

        [Test]
        public new void MultiLiteralPropertyRefreshMixedModes()
        {
            base.MultiLiteralPropertyRefreshMixedModes();
        }

        #endregion
    }
}
#endif