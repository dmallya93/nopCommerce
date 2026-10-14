using AwesomeAssertions;
using Moq;
using Nop.Core;
using Nop.Core.Domain.Directory;
using Nop.Services.Directory;
using NUnit.Framework;

namespace Nop.Tests.Nop.Services.Tests.Directory;

/// <summary>
/// Pure unit tests (no DI/SQLite) that codify the legacy nopCommerce 3.90 behavioral contract
/// for <see cref="UpdateExchangeRateTask"/> against the current implementation.
/// <see cref="ICurrencyService"/> is mocked directly so each contract item can be asserted in isolation.
/// </summary>
[TestFixture]
public class UpdateExchangeRateTaskTests
{
    private CurrencySettings _currencySettings;
    private Mock<ICurrencyService> _currencyServiceMock;

    [SetUp]
    public void SetUp()
    {
        _currencySettings = new CurrencySettings
        {
            AutoUpdateEnabled = true
        };
        _currencyServiceMock = new Mock<ICurrencyService>();
    }

    private UpdateExchangeRateTask CreateTask()
    {
        return new UpdateExchangeRateTask(_currencySettings, _currencyServiceMock.Object);
    }

    private static ExchangeRate Rate(string code, decimal rate) =>
        new() { CurrencyCode = code, Rate = rate, UpdatedOn = DateTime.UtcNow };

    private static Currency Currency(string code, decimal rate = 1m) =>
        new() { CurrencyCode = code, Rate = rate };

    private void SetupLiveRates(IList<ExchangeRate> rates) =>
        _currencyServiceMock.Setup(s => s.GetCurrencyLiveRatesAsync(null)).ReturnsAsync(rates);

    // Contract #1: AutoUpdateEnabled == false short-circuits the entire task — no provider call, no currency reads, no writes.
    [Test]
    public async Task AutoUpdateDisabled_ShouldNotCallAnyCurrencyServiceMethod()
    {
        _currencySettings.AutoUpdateEnabled = false;

        var task = CreateTask();
        await task.ExecuteAsync();

        _currencyServiceMock.Verify(s => s.GetCurrencyLiveRatesAsync(It.IsAny<string>()), Times.Never);
        _currencyServiceMock.Verify(s => s.GetCurrencyByCodeAsync(It.IsAny<string>()), Times.Never);
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Never);
    }

    // Contract #4: Every rate returned by the provider is iterated — not just the first or a filtered subset.
    [Test]
    public async Task ExecuteAsync_ShouldIterateEveryReturnedRate_NotJustTheFirst()
    {
        var rates = new List<ExchangeRate>
        {
            Rate("USD", 1.1m),
            Rate("RUB", 90m),
            Rate("GBP", 0.85m)
        };
        SetupLiveRates(rates);

        foreach (var rate in rates)
        {
            _currencyServiceMock.Setup(s => s.GetCurrencyByCodeAsync(rate.CurrencyCode))
                .ReturnsAsync(Currency(rate.CurrencyCode));
        }

        var task = CreateTask();
        await task.ExecuteAsync();

        foreach (var rate in rates)
            _currencyServiceMock.Verify(s => s.GetCurrencyByCodeAsync(rate.CurrencyCode), Times.Once);

        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Exactly(rates.Count));
    }

    // Contract #5: Currency lookup by code is case-insensitive (GetCurrencyByCodeAsync lower-invariant comparison),
    // and an unmatched code is silently skipped (continue), not an error.
    [Test]
    public async Task ExecuteAsync_ShouldSkipUnmatchedCurrencyCode_WithoutThrowing()
    {
        var rates = new List<ExchangeRate>
        {
            Rate("USD", 1.1m),
            Rate("ZZZ", 123m) // unmatched/unknown code
        };
        SetupLiveRates(rates);
        _currencyServiceMock.Setup(s => s.GetCurrencyByCodeAsync("USD")).ReturnsAsync(Currency("USD"));
        _currencyServiceMock.Setup(s => s.GetCurrencyByCodeAsync("ZZZ")).ReturnsAsync((Currency)null);

        var task = CreateTask();
        Func<Task> act = () => task.ExecuteAsync();

        await act.Should().NotThrowAsync();
        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Once);
    }

    // Contract #5 (delegation half): the task passes the exchange rate's currency code through to
    // GetCurrencyByCodeAsync verbatim, relying on the service for case-insensitive resolution rather
    // than normalizing the code itself.
    [Test]
    public async Task ExecuteAsync_ShouldPassCurrencyCodeVerbatimToLookup()
    {
        var rates = new List<ExchangeRate> { Rate("usd", 1.1m) };
        SetupLiveRates(rates);
        _currencyServiceMock.Setup(s => s.GetCurrencyByCodeAsync(It.IsAny<string>())).ReturnsAsync(Currency("USD"));

        var task = CreateTask();
        await task.ExecuteAsync();

        _currencyServiceMock.Verify(s => s.GetCurrencyByCodeAsync("usd"), Times.Once);
    }

    // Contract #6: Rate and UpdatedOnUtc are updated together, atomically, for each matched currency.
    [Test]
    public async Task ExecuteAsync_ShouldUpdateRateAndUpdatedOnUtcTogether()
    {
        var existingCurrency = Currency("USD", rate: 1m);
        existingCurrency.UpdatedOnUtc = DateTime.UtcNow.AddDays(-30);

        var rates = new List<ExchangeRate> { Rate("USD", 1.2345m) };
        SetupLiveRates(rates);
        _currencyServiceMock.Setup(s => s.GetCurrencyByCodeAsync("USD")).ReturnsAsync(existingCurrency);

        Currency persisted = null;
        _currencyServiceMock.Setup(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()))
            .Callback<Currency>(c => persisted = c)
            .Returns(Task.CompletedTask);

        var before = DateTime.UtcNow;
        var task = CreateTask();
        await task.ExecuteAsync();
        var after = DateTime.UtcNow;

        persisted.Should().NotBeNull();
        persisted.Rate.Should().Be(1.2345m);
        persisted.UpdatedOnUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // Contract #7: Each currency is persisted individually via UpdateCurrencyAsync (no batching) —
    // one call per matched currency, matching legacy's one-row-at-a-time save.
    [Test]
    public async Task ExecuteAsync_ShouldCallUpdateCurrencyAsyncOncePerMatchedCurrency_NoBatching()
    {
        var rates = new List<ExchangeRate>
        {
            Rate("USD", 1.1m),
            Rate("RUB", 90m),
            Rate("GBP", 0.85m)
        };
        SetupLiveRates(rates);

        var persistedCurrencies = new List<Currency>();
        foreach (var rate in rates)
        {
            var currency = Currency(rate.CurrencyCode);
            _currencyServiceMock.Setup(s => s.GetCurrencyByCodeAsync(rate.CurrencyCode)).ReturnsAsync(currency);
        }

        _currencyServiceMock.Setup(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()))
            .Callback<Currency>(c => persistedCurrencies.Add(c))
            .Returns(Task.CompletedTask);

        var task = CreateTask();
        await task.ExecuteAsync();

        _currencyServiceMock.Verify(s => s.UpdateCurrencyAsync(It.IsAny<Currency>()), Times.Exactly(3));
        persistedCurrencies.Select(c => c.CurrencyCode).Should().BeEquivalentTo(rates.Select(r => r.CurrencyCode));
    }

    // Contract #2: The active exchange-rate provider is resolved via ExchangeRatePluginManager.LoadPrimaryPluginAsync
    // (driven by CurrencySettings.ActiveExchangeRateProviderSystemName), throwing when none is active.
    // Contract #9 (task/service-level half): task/service-level exceptions (no active provider,
    // unset primary currency) propagate up uncaught, to be handled by ScheduleTaskRunner.
    [Test]
    public async Task ExecuteAsync_ShouldPropagate_NoActiveProviderException()
    {
        _currencyServiceMock.Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ThrowsAsync(new Exception("Active exchange rate provider cannot be loaded"));

        var task = CreateTask();
        Func<Task> act = () => task.ExecuteAsync();

        await act.Should().ThrowAsync<Exception>().WithMessage("Active exchange rate provider cannot be loaded");
    }

    // Contract #3: The primary exchange-rate currency is resolved via CurrencySettings.PrimaryExchangeRateCurrencyId,
    // throwing NopException when unset.
    // Contract #9 (task/service-level half): unset primary exchange rate currency throws NopException,
    // which must propagate out of the task rather than being swallowed.
    [Test]
    public async Task ExecuteAsync_ShouldPropagate_PrimaryCurrencyNotSetException()
    {
        _currencyServiceMock.Setup(s => s.GetCurrencyLiveRatesAsync(null))
            .ThrowsAsync(new NopException("Primary exchange rate currency is not set"));

        var task = CreateTask();
        Func<Task> act = () => task.ExecuteAsync();

        await act.Should().ThrowAsync<NopException>().WithMessage("Primary exchange rate currency is not set");
    }
}
