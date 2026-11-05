using client.Services;
using Xunit;

namespace client.Tests;

public class UpdateInstallDecisionTests
{
    [Fact]
    public void Unknown_op_selects_the_gui_fallback()
    {
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(true, false, "{\"ok\":false,\"error\":\"unknown_op\"}"),
            null);
        Assert.Equal(UpdateInstallRoute.GuiFallback, route);
    }

    [Fact]
    public void Ok_selects_service_polling()
    {
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(true, false, "{\"ok\":true,\"queued\":true}"),
            null);
        Assert.Equal(UpdateInstallRoute.PollService, route);
    }

    [Fact]
    public void Update_disabled_does_not_fall_back()
    {
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(true, false, "{\"ok\":false,\"error\":\"update_disabled\"}"),
            new UpdatePipeView(true, false, "{\"ok\":false,\"error\":\"unknown_op\"}"));
        Assert.Equal(UpdateInstallRoute.UpdatesDisabled, route);
    }

    [Fact]
    public void In_progress_does_not_start_a_second_installer()
    {
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(true, false, "{\"ok\":false,\"error\":\"unknown_op\"}"),
            new UpdatePipeView(true, false, "{\"ok\":true,\"phase\":\"downloading\",\"updateInProgress\":true}"));
        Assert.Equal(UpdateInstallRoute.AlreadyInProgress, route);
    }

    [Fact]
    public void Timeout_plus_downloading_does_not_fall_back()
    {
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(false, true, "{\"ok\":false,\"error\":\"connect_timeout\"}"),
            new UpdatePipeView(true, false, "{\"ok\":true,\"phase\":\"applying\",\"updateInProgress\":true}"));
        Assert.Equal(UpdateInstallRoute.AlreadyInProgress, route);
    }

    [Fact]
    public void Timeout_plus_status_timeout_does_not_fall_back()
    {
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(false, true, "{\"ok\":false,\"error\":\"response_timeout\"}"),
            new UpdatePipeView(false, true, "{\"ok\":false,\"error\":\"response_timeout\"}"));
        Assert.Equal(UpdateInstallRoute.ServiceUnanswered, route);
    }

    [Fact]
    public void Timeout_then_unknown_op_status_can_fall_back()
    {
        var route = UpdateInstallDecision.Decide(
            new UpdatePipeView(false, true, "{\"ok\":false,\"error\":\"connect_timeout\"}"),
            new UpdatePipeView(true, false, "{\"ok\":false,\"error\":\"unknown_op\"}"));
        Assert.Equal(UpdateInstallRoute.GuiFallback, route);
    }

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(100, 99, false)]
    [InlineData(0, 50, true)]
    public void Published_size_must_match_when_present(long published, long file, bool ok)
    {
        Assert.Equal(ok, UpdateInstallDecision.PublishedSizeMatches(published, file));
    }
}
