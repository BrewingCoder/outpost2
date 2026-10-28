using OutPost2.Infrastructure.Connections;

namespace OutPost2.Tests.Connections;

/// <summary>
/// github.com is the only provider so far where the address a person says is not the
/// address the API lives at. Getting this wrong fails late and confusingly: the site
/// answers 200 with HTML, so the error surfaces as a JSON parse failure rather than as
/// "wrong host".
/// </summary>
public sealed class GitHubUrlTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("github.com")]
    [InlineData("GitHub.COM")]
    [InlineData("https://github.com")]
    [InlineData("https://github.com/")]
    [InlineData("www.github.com")]
    [InlineData("api.github.com")]
    [InlineData("https://api.github.com/")]
    public void Anything_meaning_dot_com_normalises_to_the_api_host(string input) =>
        Assert.Equal("https://api.github.com/", GitHubConnectionService.Normalise(input));

    [Theory]
    [InlineData("ghe.example.com", "https://ghe.example.com/api/v3/")]
    [InlineData("https://ghe.example.com", "https://ghe.example.com/api/v3/")]
    [InlineData("https://ghe.example.com/", "https://ghe.example.com/api/v3/")]
    public void An_enterprise_host_gains_the_api_prefix_it_serves_from(string input, string expected) =>
        Assert.Equal(expected, GitHubConnectionService.Normalise(input));

    [Fact]
    public void An_enterprise_host_that_already_names_its_api_path_is_left_alone()
    {
        // Appending a second /api/v3 to an address the user already got right would turn
        // a working URL into a 404.
        Assert.Equal("https://ghe.example.com/api/v3/",
            GitHubConnectionService.Normalise("https://ghe.example.com/api/v3"));
    }

    [Fact]
    public void A_host_merely_containing_github_is_not_treated_as_dot_com()
    {
        // "github.internal.example.com" is somebody's Enterprise server, not github.com,
        // and sending their token to api.github.com would be a credential leak.
        Assert.Equal("https://github.internal.example.com/api/v3/",
            GitHubConnectionService.Normalise("github.internal.example.com"));
    }
}
