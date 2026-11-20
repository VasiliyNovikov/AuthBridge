using System.Text;
using AuthBridge.Common;

namespace AuthBridge.Tests;

[TestClass]
[TestCategory("Mutation")]
public sealed class OAuthTests
{
    [TestMethod]
    [DataRow("https://login.example/authorize?client_id=c&redirect_uri=http%3A%2F%2Flocalhost%3A39871&state=s", 39871)]
    [DataRow("https://login.example/authorize?redirect_uri=http://127.0.0.1:41000/callback", 41000)]
    [DataRow("https://login.example/authorize?redirect_uri=http%3A%2F%2F%5B%3A%3A1%5D%3A42000%2F", 42000)]
    [DataRow("http://localhost:43000/?welcome", 43000)]
    [DataRow("http://localhost:43000/?redirect_uri=http://localhost:42000/", 43000)]
    [DataRow("https://login.example/authorize?redirect_uri=https://127.0.0.1:41000/callback", null)]
    [DataRow("https://login.example/authorize?redirect_uri=http://app.example:41000/callback", null)]
    [DataRow("https://login.example/authorize?redirect_uri=https%3A%2F%2Fapp.example%2Fcallback", null)]
    [DataRow("https://example.com/device", null)]
    public void FindsTheLoopbackRedirectPort(string url, int? port) => Assert.AreEqual(port, OAuth.LoopbackPort(url));

    [TestMethod]
    public void LogDescriptionIncludesOnlyBoundedNonSecretParameters()
    {
        var scope = new string('a', 130);
        var url = $"https://login.example/?client_id=client+name&state=secret&scope={scope}&prompt=login&response_mode=form_post#&client_id=hidden";
        Assert.AreEqual($"client_id=client name scope={scope[..120]} prompt=login response_mode=form_post", OAuth.Describe(url));
        Assert.AreEqual("https://login.example/", OAuth.Redact(url));
    }

    [TestMethod]
    public void CancelsWithAnErrorRedirectThatCarriesTheState()
    {
        const string Url = "https://login.example/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fcallback&state=s+1";
        const string Error = "error=temporarily_unavailable&error_description=retry%20later&state=s%201";

        var query = OAuth.CancelRequest(Url, 5000, "retry later")!;
        var formPost = OAuth.CancelRequest(Url + "&response_mode=form_post", 5000, "retry later")!;

        Assert.AreEqual(("GET", $"/callback?{Error}"), (query.Method, query.Target));
        Assert.AreEqual(("POST", "/callback", Error), (formPost.Method, formPost.Target, Encoding.ASCII.GetString(formPost.Body)));
        Assert.AreEqual("localhost:5000", query.GetHeader("Host"));
        Assert.AreEqual("application/x-www-form-urlencoded", formPost.GetHeader("Content-Type"));
        Assert.IsNull(OAuth.CancelRequest("https://login.example/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A5000", 5000, "no state"));
        Assert.IsNull(OAuth.CancelRequest("https://login.example/authorize?state=s", 5000, "no redirect"));
    }
}
