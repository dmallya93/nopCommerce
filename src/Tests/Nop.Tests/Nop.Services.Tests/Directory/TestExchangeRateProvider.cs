using Nop.Core.Domain.Directory;
using Nop.Services.Directory;
using Nop.Services.Plugins;

namespace Nop.Tests.Nop.Services.Tests.Directory;

public class TestExchangeRateProvider : BasePlugin, IExchangeRateProvider
{
    /// <summary>
    /// Gets or sets the rates returned by <see cref="GetCurrencyLiveRatesAsync"/>.
    /// Tests set this before exercising the provider and must reset it in [TearDown]
    /// to avoid leaking state into other tests, since this list is static.
    /// </summary>
    public static IList<ExchangeRate> RatesToReturn { get; set; } = new List<ExchangeRate>();

    /// <summary>
    /// Gets currency live rates
    /// </summary>
    /// <param name="exchangeRateCurrencyCode">Exchange rate currency code</param>
    /// <returns>Exchange rates</returns>
    public Task<IList<ExchangeRate>> GetCurrencyLiveRatesAsync(string exchangeRateCurrencyCode)
    {
        return Task.FromResult(RatesToReturn);
    }
}