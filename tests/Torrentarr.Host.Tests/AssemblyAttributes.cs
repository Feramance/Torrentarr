using Xunit;

// Run all tests in this assembly sequentially so multiple WebApplicationFactory fixtures
// (base, auth-enabled, local-auth) do not race on static config path and each host
// loads the correct config when built.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
