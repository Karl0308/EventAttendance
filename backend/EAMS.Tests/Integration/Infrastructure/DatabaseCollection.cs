using Xunit;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Every integration test class joins this collection, which has two consequences and both are the
/// point.
///
/// <para>
/// <b>One SQL Server for the whole run.</b> Starting a container per class would cost more than the
/// suite itself.
/// </para>
///
/// <para>
/// <b>The integration tests run serially.</b> They share one database and each wipes it in
/// <c>InitializeAsync</c>; running two classes in parallel would mean one test deleting another's
/// rows mid-assertion, producing failures that do not reproduce. xUnit parallelizes <em>across</em>
/// collections, so the unit tests still run alongside these — the serialization is scoped to the
/// tests that share state, not to the suite.
/// </para>
///
/// <para>
/// Concurrency <em>within</em> a test is unaffected: the tap race test opens several connections at
/// once, which is exactly what it needs to prove.
/// </para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
