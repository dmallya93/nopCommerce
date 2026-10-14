using AwesomeAssertions;
using Nop.Core;
using Nop.Core.Domain.Directory;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using NUnit.Framework;

namespace Nop.Tests.Nop.Services.Tests.Directory;

/// <summary>
/// Integration tests that run <see cref="UpdateExchangeRateTask"/> against the real SQLite-backed
/// DI container (services resolved via <see cref="ServiceTest.GetService{T}"/>), using
/// <see cref="TestExchangeRateProvider"/> as the active exchange rate plugin so that
/// <see cref="TestExchangeRateProvider.RatesToReturn"/> drives the pipeline end-to-end.
/// </summary>
[TestFixture]
public class UpdateExchangeRateTaskIntegrationTests : ServiceTest
{
    private const string TestProviderSystemName = "CurrencyExchange.TestProvider";

    private ICurrencyService _currencyService;
    private ISettingService _settingService;

    private static readonly string[] SnapshotCurrencyCodes = { "USD", "RUB", "EUR" };

    private CurrencySettings _originalSettingsSnapshot;
    private Dictionary<string, Currency> _originalCurrencySnapshots;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _currencyService = GetService<ICurrencyService>();
        _settingService = GetService<ISettingService>();
    }

    [SetUp]
    public async Task SetUp()
    {
        TestExchangeRateProvider.RatesToReturn = new List<ExchangeRate>();

        var currentSettings = GetService<CurrencySettings>();
        _originalSettingsSnapshot = new CurrencySettings
        {
            DisplayCurrencyLabel = currentSettings.DisplayCurrencyLabel,
            PrimaryStoreCurrencyId = currentSettings.PrimaryStoreCurrencyId,
            PrimaryExchangeRateCurrencyId = currentSettings.PrimaryExchangeRateCurrencyId,
            ActiveExchangeRateProviderSystemName = currentSettings.ActiveExchangeRateProviderSystemName,
            AutoUpdateEnabled = currentSettings.AutoUpdateEnabled,
            DisplayCurrencySymbolInCurrencySelector = currentSettings.DisplayCurrencySymbolInCurrencySelector
        };

        _originalCurrencySnapshots = new Dictionary<string, Currency>();
        foreach (var currencyCode in SnapshotCurrencyCodes)
            _originalCurrencySnapshots[currencyCode] = await SnapshotAsync(currencyCode);
    }

    [TearDown]
    public async Task TearDown()
    {
        //reset the static rates list so no state leaks into other tests/fixtures sharing this process
        TestExchangeRateProvider.RatesToReturn = new List<ExchangeRate>();

        //restore the currency settings exactly as they were before this test ran
        await SaveCurrencySettingsAsync(_originalSettingsSnapshot);

        //restore any currency rows this test may have modified
        foreach (var snapshot in _originalCurrencySnapshots.Values)
            await RestoreAsync(snapshot);
    }

    /// <summary>
    /// Saves each CurrencySettings property individually via the per-property SaveSettingAsync overload.
    /// The bulk SaveSettingAsync(T settings) overload deliberately does NOT clear the settings cache
    /// (see SettingService.SaveSettingAsync&lt;T&gt; comment), so using it here would leave stale cached
    /// settings behind and make subsequent GetService&lt;CurrencySettings&gt;() calls return pre-save values.
    /// The per-property overload clears the cache by default, matching the pattern ServiceTest.InitPlugins()
    /// already uses for TaxSettings/ShippingSettings.
    /// </summary>
    private async Task SaveCurrencySettingsAsync(CurrencySettings settings)
    {
        await _settingService.SaveSettingAsync(settings, s => s.DisplayCurrencyLabel);
        await _settingService.SaveSettingAsync(settings, s => s.PrimaryStoreCurrencyId);
        await _settingService.SaveSettingAsync(settings, s => s.PrimaryExchangeRateCurrencyId);
        await _settingService.SaveSettingAsync(settings, s => s.ActiveExchangeRateProviderSystemName);
        await _settingService.SaveSettingAsync(settings, s => s.AutoUpdateEnabled);
        await _settingService.SaveSettingAsync(settings, s => s.DisplayCurrencySymbolInCurrencySelector);
    }

    private async Task<Currency> SnapshotAsync(string currencyCode)
    {
        var currency = await _currencyService.GetCurrencyByCodeAsync(currencyCode);
        if (currency == null)
            return null;

        return new Currency { Id = currency.Id, Rate = currency.Rate, UpdatedOnUtc = currency.UpdatedOnUtc };
    }

    private async Task RestoreAsync(Currency snapshot)
    {
        if (snapshot == null)
            return;

        var current = await _currencyService.GetCurrencyByIdAsync(snapshot.Id);
        if (current == null)
            return;

        current.Rate = snapshot.Rate;
        current.UpdatedOnUtc = snapshot.UpdatedOnUtc;
        await _currencyService.UpdateCurrencyAsync(current);
    }

    /// <summary>
    /// Points the active provider at the test plugin (so RatesToReturn drives the pipeline), toggles
    /// AutoUpdateEnabled as requested, persists via ISettingService, then resolves a fresh
    /// CurrencySettings/ICurrencyService pair from the container to build the task under test.
    /// </summary>
    private async Task<UpdateExchangeRateTask> CreateTaskAsync(bool autoUpdateEnabled, int? primaryExchangeRateCurrencyId = null)
    {
        var settings = GetService<CurrencySettings>();
        settings.ActiveExchangeRateProviderSystemName = TestProviderSystemName;
        settings.AutoUpdateEnabled = autoUpdateEnabled;
        if (primaryExchangeRateCurrencyId.HasValue)
            settings.PrimaryExchangeRateCurrencyId = primaryExchangeRateCurrencyId.Value;

        await SaveCurrencySettingsAsync(settings);

        var freshSettings = GetService<CurrencySettings>();
        var currencyService = GetService<ICurrencyService>();

        return new UpdateExchangeRateTask(freshSettings, currencyService);
    }

    // Contract #2: The active exchange-rate provider is resolved via ExchangeRatePluginManager.LoadPrimaryPluginAsync,
    // driven by CurrencySettings.ActiveExchangeRateProviderSystemName — pointing the setting at the test plugin
    // is what makes TestExchangeRateProvider.RatesToReturn drive the pipeline end-to-end.
    // Confirm matched currencies (EUR/USD/RUB) get their Rate/UpdatedOnUtc updated in the database after ExecuteAsync().
    [Test]
    public async Task ExecuteAsync_ShouldPersistRateAndUpdatedOnUtc_ForMatchedCurrency()
    {
        TestExchangeRateProvider.RatesToReturn = new List<ExchangeRate>
        {
            new() { CurrencyCode = "USD", Rate = 1.2345m, UpdatedOn = DateTime.UtcNow }
        };

        var task = await CreateTaskAsync(autoUpdateEnabled: true);

        //the DB round-trip truncates UpdatedOnUtc to millisecond precision, so allow a small
        //tolerance either side rather than comparing against sub-millisecond-precision timestamps
        var before = DateTime.UtcNow.AddMilliseconds(-5);
        await task.ExecuteAsync();
        var after = DateTime.UtcNow.AddMilliseconds(5);

        var usd = await _currencyService.GetCurrencyByCodeAsync("USD");
        usd.Should().NotBeNull();
        usd.Rate.Should().Be(1.2345m);
        usd.UpdatedOnUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    // Contract #5: Currency lookup by code is case-insensitive (GetCurrencyByCodeAsync lower-invariant comparison) —
    // exercised end-to-end here through the real CurrencyService implementation, not a mock.
    [Test]
    public async Task ExecuteAsync_ShouldMatchCurrencyCodeCaseInsensitively_EndToEnd()
    {
        TestExchangeRateProvider.RatesToReturn = new List<ExchangeRate>
        {
            new() { CurrencyCode = "usd", Rate = 2.5m, UpdatedOn = DateTime.UtcNow }
        };

        var task = await CreateTaskAsync(autoUpdateEnabled: true);
        await task.ExecuteAsync();

        var usd = await _currencyService.GetCurrencyByCodeAsync("USD");
        usd.Rate.Should().Be(2.5m);
    }

    // Contract #5: an unmatched code is silently skipped (continue), not an error — confirmed here with the
    // real database: no exception, and no spurious currency row is created.
    [Test]
    public async Task ExecuteAsync_ShouldNoOpForUnmatchedCurrencyCode()
    {
        var countBefore = (await _currencyService.GetAllCurrenciesAsync(true)).Count;

        TestExchangeRateProvider.RatesToReturn = new List<ExchangeRate>
        {
            new() { CurrencyCode = "ZZZ", Rate = 5m, UpdatedOn = DateTime.UtcNow }
        };

        var task = await CreateTaskAsync(autoUpdateEnabled: true);
        Func<Task> act = () => task.ExecuteAsync();

        await act.Should().NotThrowAsync();

        var countAfter = (await _currencyService.GetAllCurrenciesAsync(true)).Count;
        countAfter.Should().Be(countBefore);
    }

    // Contract #1: AutoUpdateEnabled == false short-circuits the entire task — no provider call, no currency reads,
    // no writes. Confirmed here against the real database: the currency's rate is left untouched.
    [Test]
    public async Task ExecuteAsync_ShouldPerformZeroWrites_WhenAutoUpdateDisabled()
    {
        var usdBefore = await _currencyService.GetCurrencyByCodeAsync("USD");
        var rateBefore = usdBefore.Rate;
        var updatedOnBefore = usdBefore.UpdatedOnUtc;

        TestExchangeRateProvider.RatesToReturn = new List<ExchangeRate>
        {
            new() { CurrencyCode = "USD", Rate = 999m, UpdatedOn = DateTime.UtcNow }
        };

        var task = await CreateTaskAsync(autoUpdateEnabled: false);
        await task.ExecuteAsync();

        var usdAfter = await _currencyService.GetCurrencyByCodeAsync("USD");
        usdAfter.Rate.Should().Be(rateBefore);
        usdAfter.UpdatedOnUtc.Should().Be(updatedOnBefore);
    }

    // Contract #7: Each currency is persisted individually via UpdateCurrencyAsync (no batching) — confirmed here
    // end-to-end: every matched currency in a multi-rate batch ends up with its own correct persisted rate.
    [Test]
    public async Task ExecuteAsync_ShouldPersistEachMatchedCurrencyIndividually()
    {
        TestExchangeRateProvider.RatesToReturn = new List<ExchangeRate>
        {
            new() { CurrencyCode = "USD", Rate = 1.1m, UpdatedOn = DateTime.UtcNow },
            new() { CurrencyCode = "RUB", Rate = 90m, UpdatedOn = DateTime.UtcNow },
            new() { CurrencyCode = "EUR", Rate = 1m, UpdatedOn = DateTime.UtcNow }
        };

        var task = await CreateTaskAsync(autoUpdateEnabled: true);
        await task.ExecuteAsync();

        (await _currencyService.GetCurrencyByCodeAsync("USD")).Rate.Should().Be(1.1m);
        (await _currencyService.GetCurrencyByCodeAsync("RUB")).Rate.Should().Be(90m);
        (await _currencyService.GetCurrencyByCodeAsync("EUR")).Rate.Should().Be(1m);
    }

    // Contract #3: The primary exchange-rate currency is resolved via CurrencySettings.PrimaryExchangeRateCurrencyId,
    // throwing NopException when unset — confirmed here end-to-end: pointing the id at a non-existent currency
    // means CurrencyService.GetCurrencyLiveRatesAsync cannot resolve a currency code, and the task must not
    // swallow the resulting NopException.
    [Test]
    public async Task ExecuteAsync_ShouldPropagateNopException_WhenPrimaryExchangeRateCurrencyIdUnset()
    {
        var task = await CreateTaskAsync(autoUpdateEnabled: true, primaryExchangeRateCurrencyId: 0);

        Func<Task> act = () => task.ExecuteAsync();

        await act.Should().ThrowAsync<NopException>();
    }
}
