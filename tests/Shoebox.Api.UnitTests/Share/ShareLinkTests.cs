using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Share;

namespace Shoebox.Api.UnitTests.Share
{
    // The format is the SPA's, and the SPA is the thing that has to read these back: deflate-raw
    // then base64url, in the fragment. If this drifts, a link opens on an empty diagram and nothing
    // says why.
    [TestFixture]
    public class ShareLinkTests
    {
        private const string Diagram = "flowchart TD\n  a[Orders API] -->|broken: timeout| b[(Store)]";

        [Test]
        public void RoundTrips()
        {
            ShareLink.Decode(ShareLink.Encode(Diagram)).Should().Be(Diagram);
        }

        [Test]
        public void EncodesBase64Url_WithoutPadding()
        {
            var encoded = ShareLink.Encode(Diagram);

            encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=",
                "the value rides in a URL, so + / and = are the three characters it cannot carry");
        }

        [Test]
        public void PutsTheDiagramInTheFragmentAndTheShoeboxInTheQuery()
        {
            // Deliberate split: the fragment never leaves the browser, and a pasted diagram is full
            // of real service names. The id is a random string the server needs on every run.
            var url = ShareLink.For("https://shoebox.deepcube.ai", Diagram, "abc123");

            url.Should().StartWith("https://shoebox.deepcube.ai/?shoeboxId=abc123#d=");
            ShareLink.Decode(url.Split("#d=")[1]).Should().Be(Diagram);
        }

        [Test]
        public void OmitsTheQueryWhenThereIsNoShoebox()
        {
            ShareLink.For("https://shoebox.deepcube.ai", Diagram, null)
                .Should().StartWith("https://shoebox.deepcube.ai/#d=");
        }

        [Test]
        public void DoesNotDoubleTheSlashOnAnOriginThatHasOne()
        {
            ShareLink.For("http://localhost:5168/", Diagram, null)
                .Should().StartWith("http://localhost:5168/#d=");
        }

        [Test]
        public void EscapesAShoeboxIdRatherThanTrustingIt()
        {
            ShareLink.For("https://x", Diagram, "a b&c").Should().Contain("shoeboxId=a%20b%26c");
        }

        [Test]
        public void CompressesRatherThanInflates()
        {
            // A ten line diagram should land in a few hundred characters, not more than it started.
            var repetitive = string.Join("\n", System.Linq.Enumerable.Repeat("  a[Orders API] --> b[Inventory]", 20));

            ShareLink.Encode(repetitive).Length.Should().BeLessThan(repetitive.Length);
        }
    }
}
