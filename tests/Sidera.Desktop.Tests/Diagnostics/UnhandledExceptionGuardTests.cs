using Microsoft.Extensions.Logging.Abstractions;
using Sidera.Desktop.Diagnostics;
using Sidera.Desktop.Notifications;

namespace Sidera.Desktop.Tests.Diagnostics;

/// <summary>The last line of defence of the interface: an exception nobody handled is said and the application goes on.</summary>
public sealed class UnhandledExceptionGuardTests
{
    private static (UnhandledExceptionGuard Guard, NotificationService Notifications) Create()
    {
        var notifications = new NotificationService(a => a(), null);
        var guard = new UnhandledExceptionGuard(NullLogger.Instance);
        guard.Attach(notifications);
        return (guard, notifications);
    }

    [Fact]
    public void AnOrdinaryException_IsSaidAsAnError_AndTheApplicationGoesOn()
    {
        var (guard, notifications) = Create();

        var goOn = guard.Handle(new InvalidOperationException("Nullable object must have a value."));

        Assert.True(goOn);
        var message = Assert.Single(notifications.History);
        Assert.Equal(NotificationSeverity.Error, message.Severity);
        Assert.Equal("Something went wrong", message.Title);
        Assert.Contains("Nullable object must have a value.", message.Detail);
    }

    [Fact]
    public void TheSameProblemAgainAndAgain_IsOneMessage_NotAStack()
    {
        var (guard, notifications) = Create();

        for (var i = 0; i < 5; i++)
        {
            Assert.True(guard.Handle(new InvalidOperationException("again")));
        }

        Assert.Single(notifications.Visible);
    }

    [Theory]
    [InlineData(typeof(OutOfMemoryException))]
    [InlineData(typeof(InsufficientExecutionStackException))]
    public void WhatCannotBeSurvived_IsPassedOn(Type type)
    {
        var (guard, notifications) = Create();

        Assert.False(guard.Handle((Exception)Activator.CreateInstance(type)!));
        Assert.Empty(notifications.History);
    }

    [Fact]
    public void BeforeTheMessagesExist_ItIsOnlyLogged_AndTheApplicationGoesOn()
    {
        var guard = new UnhandledExceptionGuard(NullLogger.Instance);

        Assert.True(guard.Handle(new InvalidOperationException("early")));
        guard.HandleUnobserved(new InvalidOperationException("early task"));
    }

    [Fact]
    public void AFailedTask_IsSaidAsAnError()
    {
        var (guard, notifications) = Create();

        guard.HandleUnobserved(new InvalidOperationException("background"));

        Assert.Equal("A background task failed", Assert.Single(notifications.History).Title);
    }
}
