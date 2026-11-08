using Xunit;

// Disable parallel test execution for integration tests since they share the local SQL Server database.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
