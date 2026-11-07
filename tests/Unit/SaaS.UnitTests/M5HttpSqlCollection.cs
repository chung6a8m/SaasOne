using Xunit;
namespace SaaS.UnitTests;

// One platform installation per SQL process; both native HTTP and plan tests share it.
[CollectionDefinition("M5 HTTP SQL", DisableParallelization = true)]
public sealed class M5HttpSqlCollection : ICollectionFixture<M1SqlFixture> { }
