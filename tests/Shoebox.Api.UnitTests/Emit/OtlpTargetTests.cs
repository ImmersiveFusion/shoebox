using FluentAssertions;
using NUnit.Framework;
using Shoebox.Api.Emit;

namespace Shoebox.Api.UnitTests.Emit
{
    /// <summary>
    /// These mirror cmd/snowglobe/endpoint_test.go on purpose. The two tools are
    /// meant to resolve a destination the same way, and the cheapest way to keep
    /// that true is to assert the same things about both.
    /// </summary>
    [TestFixture]
    public class OtlpTargetTests
    {
        [SetUp]
        [TearDown]
        public void ClearEnvironment()
        {
            foreach (var name in new[]
                     {
                         "OTEL_EXPORTER_OTLP_ENDPOINT",
                         "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
                         "OTEL_EXPORTER_OTLP_HEADERS",
                     })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        [Test]
        public void A_Bare_Host_Port_Gets_Https_Not_A_Guess_At_Plaintext()
        {
            // Snowglobe defaults to TLS unless -insecure says otherwise. Guessing
            // plaintext for somebody who typed a hostname would silently downgrade
            // their traffic, which is not a default anyone should have to discover.
            OtlpTarget.TryParseEndpoint("otlp.example.com:4317", out var uri, out _).Should().BeTrue();

            uri!.Scheme.Should().Be("https");
            uri.Host.Should().Be("otlp.example.com");
            uri.Port.Should().Be(4317);
        }

        [Test]
        public void An_Explicit_Scheme_Is_Honored()
        {
            OtlpTarget.TryParseEndpoint("http://localhost:4318/v1/traces", out var uri, out _).Should().BeTrue();

            uri!.Scheme.Should().Be("http");
            uri.Port.Should().Be(4318);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("ftp://otlp.example.com")]
        [TestCase("not a url at all")]
        public void Nonsense_Is_Refused_With_A_Reason_Rather_Than_Guessed_At(string raw)
        {
            OtlpTarget.TryParseEndpoint(raw, out _, out var error).Should().BeFalse();

            error.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void An_Explicit_Endpoint_Beats_The_Environment()
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "https://from-env:4317");

            var target = OtlpTarget.Resolve("https://explicit:4317", null, out _);

            target!.Endpoint.Host.Should().Be("explicit");
        }

        [Test]
        public void The_Signal_Specific_Variable_Beats_The_General_One()
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", "https://general:4317");
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "https://traces:4317");

            var target = OtlpTarget.Resolve(null, null, out _);

            target!.Endpoint.Host.Should().Be("traces");
        }

        [Test]
        public void No_Endpoint_Anywhere_Resolves_To_Nothing_Rather_Than_Localhost()
        {
            // An unconfigured instance emits nothing instead of retrying forever
            // against a port nobody is listening on.
            OtlpTarget.Resolve(null, null, out _).Should().BeNull();
        }

        [Test]
        public void Headers_Fall_Back_To_The_Standard_Variable()
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "api-key=SECRET,x-team=platform");

            var target = OtlpTarget.Resolve("https://otlp.example.com:4317", null, out _);

            target!.Headers.Should().Be("api-key=SECRET,x-team=platform");
        }

        [Test]
        public void Explicit_Headers_Beat_The_Environment()
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS", "api-key=FROM_ENV");

            var target = OtlpTarget.Resolve("https://otlp.example.com:4317", "api-key=EXPLICIT", out _);

            target!.Headers.Should().Be("api-key=EXPLICIT");
        }

        [Test]
        public void Header_Parsing_Matches_The_Snowglobe_Format()
        {
            OtlpTarget.NormalizeHeaders(" api-key = SECRET , x-team=platform ")
                .Should().Be("api-key=SECRET,x-team=platform");
        }

        [Test]
        public void A_Malformed_Pair_Is_Dropped_Here_Rather_Than_Confusing_The_Exporter()
        {
            OtlpTarget.NormalizeHeaders("api-key=SECRET,garbage,=novalue,x=1")
                .Should().Be("api-key=SECRET,x=1");
        }

        [Test]
        public void A_Value_Containing_An_Equals_Sign_Survives()
        {
            // Base64 and JWTs both end in padding. Cutting on the last = would
            // truncate the credential and the failure would look like a bad key.
            OtlpTarget.NormalizeHeaders("authorization=Bearer abc==").Should().Be("authorization=Bearer abc==");
        }

        [TestCase("http://127.0.0.1:4318")]
        [TestCase("http://localhost:4318")]
        [TestCase("http://10.0.0.5:4317")]
        [TestCase("http://192.168.1.10:4317")]
        [TestCase("http://172.16.5.4:4317")]
        [TestCase("http://169.254.169.254")]
        public void Private_Addresses_Are_Refused_Off_A_Developer_Machine(string raw)
        {
            // A hosted instance taking an endpoint from a stranger is a server-side
            // request forgery primitive. 169.254.169.254 is the cloud metadata
            // service, which is the reason this list is not optional.
            OtlpTarget.TryParseEndpoint(raw, out var uri, out _).Should().BeTrue();

            OtlpTarget.IsReachableTarget(uri!, isDevelopment: false, out var error).Should().BeFalse();
            error.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void Localhost_Is_Fine_In_Development_Because_That_Is_The_Whole_Point()
        {
            OtlpTarget.TryParseEndpoint("http://localhost:4318", out var uri, out _).Should().BeTrue();

            OtlpTarget.IsReachableTarget(uri!, isDevelopment: true, out _).Should().BeTrue();
        }

    }
}
