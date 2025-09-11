# BTCPayServer Boltz Plugin Tests

This project contains automated tests for the BTCPayServer Boltz plugin, leveraging the testing infrastructure from BTCPayServer.Tests.

## Test Structure

### Test Categories

- **Fast Tests** (`[Trait("Fast", "Fast")]`): Unit tests that run quickly without external dependencies
- **Integration Tests** (`[Trait("Integration", "Integration")]`): Tests that verify component integration

### Test Classes

#### `BoltzTestBase`

Base class for all Boltz plugin tests that extends `UnitTestBase` from BTCPayServer.Tests. Provides:

- Network provider setup with Boltz plugin
- Server tester creation with Boltz plugin enabled
- Selenium and Playwright testers with Boltz plugin

#### `BoltzTestUtils`

Utility class with helper methods for:

- Getting Boltz services from server testers
- Creating test settings and configurations
- Setting up test stores with Boltz configuration
- Assertion helpers for Boltz-specific objects

#### `BoltzServiceTests`

Tests for the core `BoltzService` functionality:

- Service initialization
- Settings management (get/set/remove)
- Store configuration checking
- Server settings management
- Address generation
- Lightning client creation

#### `BoltzControllerTests`

Tests for the `BoltzController` web endpoints:

- Status page access
- Configuration page access
- Setup workflow pages
- Admin page access (with proper authorization)
- Wallet management pages

#### `BoltzPluginTests`

Tests for the `BoltzPlugin` itself:

- Plugin version and dependencies
- Service registration
- UI extension registration
- Transaction link provider registration

#### `BoltzIntegrationTests`

Integration tests that verify complete workflows:

- End-to-end store setup
- Multiple store management
- Configuration changes
- Error handling scenarios

## Running Tests

### Prerequisites

1. Ensure you have the BTCPayServer test infrastructure set up
2. Install required test dependencies (xUnit, Selenium, Playwright)
3. Configure test environment variables if needed

### Running All Tests

```bash
dotnet test
```

### Running Specific Test Categories

```bash
# Run only fast tests
dotnet test --filter "Trait=Fast"

# Run only integration tests
dotnet test --filter "Trait=Integration"
```

### Running Individual Test Classes

```bash
# Run BoltzService tests
dotnet test --filter "ClassName=BTCPayServer.Plugins.Boltz.Tests.BoltzServiceTests"

# Run BoltzController tests
dotnet test --filter "ClassName=BTCPayServer.Plugins.Boltz.Tests.BoltzControllerTests"
```

## Test Environment

The tests use the same infrastructure as BTCPayServer.Tests:

- In-memory database for fast execution
- Mock services where appropriate
- Real BTCPayServer components for integration testing
- Proper cleanup and isolation between tests

## Adding New Tests

When adding new tests:

1. **Choose the appropriate base class**: Use `BoltzTestBase` for most tests
2. **Add proper traits**: Use `[Trait("Fast", "Fast")]` for unit tests, `[Trait("Integration", "Integration")]` for integration tests
3. **Use test utilities**: Leverage `BoltzTestUtils` for common setup operations
4. **Follow naming conventions**: Use descriptive test method names that explain what is being tested
5. **Clean up resources**: Use `using` statements for `ServerTester` instances to ensure proper cleanup

## Example Test

```csharp
[Fact]
public async Task CanCreateNewBoltzConfiguration()
{
    using var serverTester = CreateServerTesterWithBoltz();
    var storeId = await serverTester.CreateTestStore();

    var boltzService = await serverTester.GetBoltzService();
    var settings = BoltzTestUtils.CreateTestBoltzSettings();

    await boltzService.Set(storeId, settings);

    Assert.True(boltzService.StoreConfigured(storeId));
    var retrievedSettings = boltzService.GetSettings(storeId);
    BoltzTestUtils.AssertBoltzSettingsEqual(settings, retrievedSettings);
}
```

## Continuous Integration

These tests are designed to run in CI environments:

- No external dependencies required for fast tests
- Proper timeout handling
- Clean resource management
- Deterministic test execution

## Debugging Tests

To debug tests:

1. Set breakpoints in your test methods
2. Run tests in debug mode: `dotnet test --logger "console;verbosity=detailed"`
3. Use the test output helper for logging: `TestLogs.LogInformation("Debug message")`
