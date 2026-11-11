using SaaS.Orders;
using Xunit;
namespace SaaS.UnitTests;

public sealed class M7LoadNumberTests
{
    [Fact]
    public void Generated_numbers_match_the_existing_canonical_business_contract()
    {
        foreach (var slug in new[] { "alpha", "beta", "gamma", "delta" })
        {
            var number = M7LoadDriver.DraftNumber("M7LOAD-ab12-", slug, 24, 1);
            var draft = new OrderDraft(number, "Fixture", 42m, "USD", "");
            var normalized = OrderPolicy.Normalize(new(OrderAction.Create, "m7-canonical-number", Draft: draft));
            Assert.Equal(number, normalized.Draft!.Number);
            Assert.Equal(number.ToUpperInvariant(), number);
        }
    }
}
