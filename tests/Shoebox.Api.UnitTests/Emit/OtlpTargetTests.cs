using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shoebox.Api.Emit;

namespace Shoebox.Api.UnitTests.Emit
{
    /// <summary>
    /// These mirror cmd/snowglobe/endpoint_test.go on purpose. Both tools are
    /// configured by whoever runs them, in the same two formats and the same
    /// precedence, and the cheapest way to keep that true is to assert the same
    /// things about both.
    /// </summary>
    [TestFixture]
    public class OtlpTargetTests
    {
        private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
            new ConfigurationBuilder()
                .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
                .Build();

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
        public void App_Settings_Win_Over_The_Standard_Variables()
        {
            var config = Config(
                ("Otlp:Endpoint", "https://from-appsettings:4317"),
                ("OTEL_EXPORTER_OTLP_ENDPOINT", "https://from-env:4317"));

            OtlpTarget.FromConfiguration(config, out _)!.Endpoint.Host.Should().Be("from-appsettings");
        }

        [Test]
        public void The_Standard_Variables_Are_Read_When_App_Settings_Are_Empty()
        {
            // An environment already configured for OpenTelemetry needs no
            // Shoebox-specific setup.
            var config = Config(("OTEL_EXPORTER_OTLP_ENDPOINT", "https://from-env:4317"));

            OtlpTarget.FromConfiguration(config, out _)!.Endpoint.Host.Should().Be("from-env");
        }

        [Test]
        public void The_Signal_Specific_Variable_Beats_The_General_One()
        {
            var config = Config(
                ("OTEL_EXPORTER_OTLP_ENDPOINT", "https://general:4317"),
                ("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "https://traces:4317"));

            OtlpTarget.FromConfiguration(config, out _)!.Endpoint.Host.Should().Be("traces");
        }

        [Test]
        public void Nothing_Configured_Resolves_To_Nothing_Rather_Than_Localhost()
        {
            // An unconfigured instance emits nothing instead of retrying forever
            // against a port nobody is listening on.
            OtlpTarget.FromConfiguration(Config(), out var error).Should().BeNull();
            error.Should().BeNull();
        }

        [Test]
        public void An_Empty_Endpoint_Setting_Counts_As_Unconfigured()
        {
            // The shipped appsettings.json carries the key with an empty value so it
            // is discoverable. That must not read as a configured endpoint.
            OtlpTarget.FromConfiguration(Config(("Otlp:Endpoint", "")), out _).Should().BeNull();
        }

        [Test]
        public void A_Bad_Endpoint_Reports_Why_So_Startup_Can_Refuse()
        {
            OtlpTarget.FromConfiguration(Config(("Otlp:Endpoint", "ftp://nope")), out var error).Should().BeNull();

            error.Should().NotBeNullOrWhiteSpace();
        }

        [Test]
        public void Headers_Come_From_App_Settings_Or_The_Standard_Variable()
        {
            var fromSettings = Config(
                ("Otlp:Endpoint", "https://otlp.example.com:4317"),
                ("Otlp:Headers", "api-key=FROM_SETTINGS"),
                ("OTEL_EXPORTER_OTLP_HEADERS", "api-key=FROM_ENV"));
            OtlpTarget.FromConfiguration(fromSettings, out _)!.Headers.Should().Be("api-key=FROM_SETTINGS");

            var fromEnv = Config(
                ("Otlp:Endpoint", "https://otlp.example.com:4317"),
                ("OTEL_EXPORTER_OTLP_HEADERS", "api-key=SECRET,x-team=platform"));
            OtlpTarget.FromConfiguration(fromEnv, out _)!.Headers.Should().Be("api-key=SECRET,x-team=platform");
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
    }
}
