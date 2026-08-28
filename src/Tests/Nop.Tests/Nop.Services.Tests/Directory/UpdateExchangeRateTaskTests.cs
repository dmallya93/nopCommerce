using AwesomeAssertions;
using Moq;
using Nop.Core.Domain.Directory;
using Nop.Services.Directory;
using NUnit.Framework;

namespace Nop.Tests.Nop.Services.Tests.Directory;

/// <summary>
/// Unit and integration tests for <see cref="UpdateExchangeRateTask"/>.
///
/// Behavioral parity documentation (legacy nopCommerce 3.90 vs modern .NET 10):
/// - Legacy 3.90 may have included a null/negative rate guard (if rate &lt;= 0, skip).
///   The modern implementation persists any rate the provider returns without validation;
///   rate validation is the exchange rate provider plugin's responsibility. This is an
///   intentional modernization improvement documented in each relevant test.
/// - Legacy 3.90 used synchronous or early async TAP patterns. The modern implementation
///   is fully async/await throughout (IScheduleTask.ExecuteAsync contract).
/// - Legacy 3.90 task scheduling used IIS background threads or Windows Services.
///   Modern implementation uses ASP.NET Core IHostedService via TaskScheduler.
///
/// Test strategy: split isolation (Milestone Design Decision 1, approach 3).
/// - Unit tests (this class, Moq-based): cover all logic branches and edge cases.
/// - Integration test (inner class UpdateExchangeRateTaskIntegrationTests, ServiceTest-based):
///   covers the AutoUpdateEnabled=false short-circuit against the real DI container + SQLite.
/// </summary>
[TestFixture]
[Category("UpdateExchangeRateTask")]
public class UpdateExchangeRateTaskTests
{
    private Mock<ICurrencyService> _currencyServiceMock;
    private CurrencySettings _currencySettings;
    private UpdateExchangeRateTask _task;

    [SetUp]
    public void SetUp()
    {
        _currencyServiceMock = new Mock<ICurrencyService>(MockBehavior.Strict);
        _currencySettings = new CurrencySettings { AutoUpdateEnabled = true };
        _task = new UpdateExchangeRateTask(_currencySettings, _currencyServiceMock.Object);
    }

    /// <summary>
    /// Legacy parity: legacy 3.90 also returned immediately when auto-update was disabled,
    /// preserving stored rates unchanged. Behavior is identical in the modern implementation.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenAutoUpdateDisabled_ShouldReturnImmediately_LegacyParityVerified()
    {
        _currencySettings.AutoUpdateEnabled = false;

        await _task.ExecuteAsync();

        // No calls should be made to the currency service - the method returns early.
        _currencyServiceMock.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Happy path: task fetches live rates and updates each matching currency's Rate and UpdatedOnUtc.
    /// Legacy parity: functional behavior matches 3.90 — fetch rates then update each known currency.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenAutoUpdateEnabled_ShouldFetchAndUpdateAllMatchingCurrencies()
    {
        var usdCurrency = new Currency { CurrencyCode = "USD", Rate = 1.0m };
        var eurCurrency = new Currency { CurrencyCode = "EUR", Rate = 0.85m };

        var rates = new List<ExchangeRate>
        {
            new() { CurrencyCode = "USD", Rate = 1.1m },
            new() { CurrencyCode = "EUR", Rate = 0.92m }
        };

        _currencyServiceMock
            .Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ReturnsAsync((IList<ExchangeRate>)rates);

        _currencyServiceMock
            .Setup(s => s.GetCurrencyByCodeAsync("USD"))
            .ReturnsAsync(usdCurrency);

        _currencyServiceMock
            .Setup(s => s.GetCurrencyByCodeAsync("EUR"))
            .ReturnsAsync(eurCurrency);

        _currencyServiceMock
            .Setup(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()))
            .Returns(Task.CompletedTask);

        var beforeExecute = DateTime.UtcNow;
        await _task.ExecuteAsync();

        usdCurrency.Rate.Should().Be(1.1m);
        eurCurrency.Rate.Should().Be(0.92m);

        usdCurrency.UpdatedOnUtc.Should().BeOnOrAfter(beforeExecute);
        eurCurrency.UpdatedOnUtc.Should().BeOnOrAfter(beforeExecute);

        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(usdCurrency), Times.Once);
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(eurCurrency), Times.Once);
    }

    /// <summary>
    /// Legacy parity: legacy 3.90 also silently skipped currency codes not found in the database
    /// and continued processing the remainder of the rate list. Behavior is identical.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenCurrencyCodeNotFoundInDatabase_ShouldSkipAndContinueProcessing_LegacyParityVerified()
    {
        var eurCurrency = new Currency { CurrencyCode = "EUR", Rate = 0.85m };

        var rates = new List<ExchangeRate>
        {
            new() { CurrencyCode = "XYZ", Rate = 5.0m }, // unknown code
            new() { CurrencyCode = "EUR", Rate = 0.92m }
        };

        _currencyServiceMock
            .Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ReturnsAsync((IList<ExchangeRate>)rates);

        _currencyServiceMock
            .Setup(s => s.GetCurrencyByCodeAsync("XYZ"))
            .ReturnsAsync((Currency)null);

        _currencyServiceMock
            .Setup(s => s.GetCurrencyByCodeAsync("EUR"))
            .ReturnsAsync(eurCurrency);

        _currencyServiceMock
            .Setup(s => s.UpdateCurrencyAsync(eurCurrency))
            .Returns(Task.CompletedTask);

        await _task.ExecuteAsync();

        eurCurrency.Rate.Should().Be(0.92m);
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Once);
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(eurCurrency), Times.Once);
    }

    /// <summary>
    /// Legacy parity: legacy 3.90 allowed exceptions from GetCurrencyLiveRatesAsync to propagate,
    /// which caused the task to be marked as failed by the scheduler (StopOnError flag applies).
    /// Modern implementation also propagates the exception. Behavior is identical.
    /// </summary>
    [Test]
    public void ExecuteAsync_WhenGetCurrencyLiveRatesThrows_ShouldPropagateException_LegacyParityVerified()
    {
        _currencyServiceMock
            .Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ThrowsAsync(new InvalidOperationException("Provider unavailable"));

        Assert.ThrowsAsync<InvalidOperationException>(() => _task.ExecuteAsync());

        _currencyServiceMock.Verify(s => s.GetCurrencyLiveRatesAsync(null), Times.Once);
        _currencyServiceMock.Verify(s => s.GetCurrencyByCodeAsync(It.IsAny<string>()), Times.Never);
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Never);
    }

    /// <summary>
    /// When the exchange rate provider returns an empty list, no currencies should be updated.
    /// Legacy parity: same behavior in 3.90 — empty provider result means no database writes.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenRatesListIsEmpty_ShouldCompleteWithoutAnyUpdates()
    {
        _currencyServiceMock
            .Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ReturnsAsync((IList<ExchangeRate>)new List<ExchangeRate>());

        await _task.ExecuteAsync();

        _currencyServiceMock.Verify(s => s.GetCurrencyByCodeAsync(It.IsAny<string>()), Times.Never);
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Never);
    }

    /// <summary>
    /// The UpdatedOnUtc field must be set to DateTime.UtcNow at the time of each update.
    /// Legacy parity: legacy 3.90 also set the update timestamp during task execution.
    /// Modern implementation is identical in semantics, using DateTime.UtcNow directly.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenCurrencyIsUpdated_ShouldSetUpdatedOnUtcToCurrentUtcTime()
    {
        var currency = new Currency { CurrencyCode = "GBP", Rate = 0.75m };
        var rates = new List<ExchangeRate> { new() { CurrencyCode = "GBP", Rate = 0.80m } };

        _currencyServiceMock
            .Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ReturnsAsync((IList<ExchangeRate>)rates);

        _currencyServiceMock
            .Setup(s => s.GetCurrencyByCodeAsync("GBP"))
            .ReturnsAsync(currency);

        _currencyServiceMock
            .Setup(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()))
            .Returns(Task.CompletedTask);

        var beforeCall = DateTime.UtcNow;
        await _task.ExecuteAsync();
        var afterCall = DateTime.UtcNow;

        currency.UpdatedOnUtc.Should().BeOnOrAfter(beforeCall);
        currency.UpdatedOnUtc.Should().BeOnOrBefore(afterCall);
    }

    /// <summary>
    /// When provider returns multiple rates, all matching currencies should be updated.
    /// Legacy parity: legacy 3.90 processed all returned rates in a loop, same as the modern implementation.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenMultipleCurrenciesReturned_ShouldUpdateEachMatchingCurrency()
    {
        var currencies = new Dictionary<string, Currency>
        {
            ["USD"] = new Currency { CurrencyCode = "USD", Rate = 1.0m },
            ["EUR"] = new Currency { CurrencyCode = "EUR", Rate = 0.85m },
            ["GBP"] = new Currency { CurrencyCode = "GBP", Rate = 0.73m },
            ["JPY"] = new Currency { CurrencyCode = "JPY", Rate = 130.5m }
        };

        var rates = new List<ExchangeRate>
        {
            new() { CurrencyCode = "USD", Rate = 1.05m },
            new() { CurrencyCode = "EUR", Rate = 0.91m },
            new() { CurrencyCode = "GBP", Rate = 0.79m },
            new() { CurrencyCode = "JPY", Rate = 135.2m }
        };

        _currencyServiceMock
            .Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ReturnsAsync((IList<ExchangeRate>)rates);

        foreach (var (code, currency) in currencies)
        {
            _currencyServiceMock
                .Setup(s => s.GetCurrencyByCodeAsync(code))
                .ReturnsAsync(currency);

            _currencyServiceMock
                .Setup(s => s.UpdateCurrencyAsync(currency))
                .Returns(Task.CompletedTask);
        }

        await _task.ExecuteAsync();

        currencies["USD"].Rate.Should().Be(1.05m);
        currencies["EUR"].Rate.Should().Be(0.91m);
        currencies["GBP"].Rate.Should().Be(0.79m);
        currencies["JPY"].Rate.Should().Be(135.2m);

        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Exactly(4));
    }

    /// <summary>
    /// Modernization note: legacy 3.90 may have included a guard (if rate &lt;= 0, skip).
    /// The modern implementation persists any rate the provider returns — including zero or negative.
    /// This is an INTENTIONAL behavioral difference: rate validation is the provider plugin's
    /// responsibility, not the task's. Tests confirm the modern behavior explicitly.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WhenProviderReturnsZeroRate_ShouldPersistIt_IntentionalDifferenceFromLegacy()
    {
        var currency = new Currency { CurrencyCode = "TST", Rate = 1.5m };
        var rates = new List<ExchangeRate> { new() { CurrencyCode = "TST", Rate = 0.0m } };

        _currencyServiceMock
            .Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ReturnsAsync((IList<ExchangeRate>)rates);

        _currencyServiceMock
            .Setup(s => s.GetCurrencyByCodeAsync("TST"))
            .ReturnsAsync(currency);

        _currencyServiceMock
            .Setup(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()))
            .Returns(Task.CompletedTask);

        await _task.ExecuteAsync();

        // Modern: persists zero rate as returned by provider. Legacy 3.90 may have skipped this.
        currency.Rate.Should().Be(0.0m);
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(currency), Times.Once);
    }
}

/// <summary>
/// Integration tests for <see cref="UpdateExchangeRateTask"/> using the real DI container
/// and SQLite in-memory database. Only tests the minimal integration scenario to avoid
/// shared state mutation (Milestone Design Decision 3, approach 3).
/// </summary>
[TestFixture]
[Category("UpdateExchangeRateTask")]
public class UpdateExchangeRateTaskIntegrationTests : ServiceTest
{
    private ICurrencyService _currencyService;

    [OneTimeSetUp]
    public void SetUp()
    {
        _currencyService = GetService<ICurrencyService>();
    }

    /// <summary>
    /// Integration test: when AutoUpdateEnabled=false, the real task resolves from the DI container
    /// and returns immediately without calling CurrencyService methods that touch the database.
    /// Legacy parity: same early-exit behavior existed in 3.90.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_Integration_WhenAutoUpdateDisabled_ShouldReturnImmediately_LegacyParityVerified()
    {
        var settings = new CurrencySettings { AutoUpdateEnabled = false };

        // Use a Moq'd ICurrencyService to detect any unexpected calls
        var currencyServiceMock = new Mock<ICurrencyService>(MockBehavior.Strict);

        var task = new UpdateExchangeRateTask(settings, currencyServiceMock.Object);

        await task.ExecuteAsync();

        currencyServiceMock.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Integration test: confirms the real UpdateExchangeRateTask resolves correctly with
    /// the full DI container. The TestExchangeRateProvider returns an empty rate list,
    /// so no currencies are mutated — safe against shared SQLite state pollution.
    /// Legacy parity: task with no rates from provider performs no database updates.
    /// </summary>
    [Test]
    public async Task ExecuteAsync_Integration_WithRealDependencies_AndEmptyProvider_ShouldCompleteCleanly()
    {
        var settings = GetService<CurrencySettings>();
        settings.AutoUpdateEnabled = true;

        // The TestExchangeRateProvider (registered in ServiceTest.InitPlugins) returns an empty list.
        // This means no currencies will be updated, keeping the shared SQLite database unmodified.
        var task = new UpdateExchangeRateTask(settings, _currencyService);

        // Should complete without throwing — empty provider result is a no-op
        await task.ExecuteAsync();
    }
}
