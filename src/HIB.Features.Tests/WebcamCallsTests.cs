using HIB.Features.Abstractions;
using HIB.Features.Adb;
using HIB.Features.CallRelay;
using HIB.Features.Camera;

namespace HIB.Features.Tests;

public class DialNumberTests
{
    [Theory]
    [InlineData("+966 50 123 4567", "+966501234567")]
    [InlineData("(050) 123-45.67", "0501234567")]
    [InlineData("٠٥٠١٢٣٤٥٦٧", "0501234567")]
    [InlineData("*#06#", "*#06#")]
    [InlineData("0501234567,1234", "0501234567,1234")]
    [InlineData("123\n456", "123456")]
    public void Sanitize_Accepts(string input, string expected) => Assert.Equal(expected, DialNumber.Sanitize(input));

    [Theory]
    [InlineData("12+34")]
    [InlineData("123; reboot")]
    [InlineData("123`id`")]
    [InlineData("123$(id)")]
    [InlineData("123 && rm")]
    [InlineData("123|sh")]
    [InlineData("123'x'")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("+")]
    [InlineData(",123")]
    [InlineData("tel:123")]
    [InlineData(null)]
    public void Sanitize_Rejects(string? input) => Assert.Null(DialNumber.Sanitize(input));

    [Fact]
    public void Sanitize_RejectsTooLong() => Assert.Null(DialNumber.Sanitize(new string('1', 41)));

    [Fact]
    public void TelUri_EncodesStarAndHash() => Assert.Equal("tel:%2A%2306%23", DialNumber.TelUri("*#06#"));

    [Fact]
    public void CallArguments_AreShellInert()
    {
        var args = DialNumber.CallArguments("10.0.0.2:5555", "+1 (555) 010-0000");
        Assert.Equal(new[] { "-s", "10.0.0.2:5555", "shell", "am", "start", "-a", "android.intent.action.CALL", "-d", "tel:+15550100000" }, args);
        Assert.Matches("^tel:[0-9+,%A-F]+$", DialNumber.CallArguments("s", "*21*0501234567#")![^1]);
        Assert.Null(DialNumber.CallArguments("s", "1;reboot"));
    }
}

public class CallControlTests
{
    [Fact]
    public void Keys_MapAndGuard()
    {
        Assert.Equal(new[] { "-s", "s", "shell", "input", "keyevent", "KEYCODE_CALL" }, CallKeys.AdbArguments(CallKeyAction.Answer, "s"));
        Assert.Equal("KEYCODE_ENDCALL", CallKeys.Keycode(CallKeyAction.Decline));
        Assert.Equal("KEYCODE_ENDCALL", CallKeys.Keycode(CallKeyAction.End));
        Assert.Equal("accept", CallKeys.ProtocolAction(CallKeyAction.Answer));
        Assert.True(CallKeys.IsAllowed(CallKeyAction.Answer, PhoneCallState.Ringing));
        Assert.False(CallKeys.IsAllowed(CallKeyAction.Answer, PhoneCallState.OffHook));
        Assert.False(CallKeys.IsAllowed(CallKeyAction.Answer, PhoneCallState.Idle));
        Assert.True(CallKeys.IsAllowed(CallKeyAction.End, PhoneCallState.OffHook));
        Assert.False(CallKeys.IsAllowed(CallKeyAction.End, PhoneCallState.Idle));
    }

    [Fact]
    public void CallState_ParsesHighestAcrossSims()
    {
        Assert.Equal(PhoneCallState.OffHook, PhoneCallStates.Parse("  mCallState=0\n  mCallIncomingNumber=\n  mCallState=2\n  mForegroundCallState=0\n"));
        Assert.Equal(PhoneCallState.Ringing, PhoneCallStates.Parse("    mCallState=1\r\n    mCallState=0\r\n"));
        Assert.Equal(PhoneCallState.Idle, PhoneCallStates.Parse("    mCallState=0"));
        Assert.Null(PhoneCallStates.Parse("nothing"));
    }

    [Fact]
    public void Contacts_ParseDedupeAndFilter()
    {
        const string rows = """
            Row: 0 display_name=Mom, data1=+966 50 111 2222
            Row: 1 display_name=Smith, John, data1=0502223333
            Row: 2 display_name=Mom, data1=+966501112222
            Row: 3 display_name=NULL, data1=1234
            Row: 4 display_name=Nobody, data1=NULL
            garbage
            """;
        var list = ContactsQuery.Parse(rows);
        Assert.Equal(3, list.Count);
        Assert.Contains(new PhoneContact("Smith, John", "0502223333"), list);
        Assert.Contains(list, c => c.Name == "1234");
        Assert.Equal("Smith, John", Assert.Single(ContactsQuery.Filter(list, "smi")).Name);
        Assert.Equal("Mom", ContactsQuery.Filter(list, "1112").First().Name);
        Assert.Empty(ContactsQuery.Filter(list, "zz"));
    }

    [Fact]
    public void CallAudio_Arguments()
    {
        var a = CallAudioArgs.Build("s", "voice-call");
        Assert.Contains("--audio-source=voice-call", a);
        Assert.Contains("--no-video", a);
        Assert.Contains("--require-audio", a);
        Assert.Equal(new[] { "voice-call", "voice-call-downlink" }, CallAudioArgs.Sources);
    }

    [Fact]
    public void CallAudio_FollowsCallEventsWithoutAdb()
    {
        var settings = new InMemorySettingsStore();
        var link = new FakePhoneLink { IsConnected = true };
        using var calls = new CallRelayService(new AdbService(new AdbLocator(), settings), link, null, settings);
        calls.Audio.AutoHearCalls = true;
        link.Receive("call_event", """{"eventId":"1","direction":"incoming","state":"offhook"}""");
        Assert.True(calls.Audio.InCall);
        Assert.False(calls.Audio.IsActive);                // no ADB → can't start, reports why
        Assert.Contains("ADB", calls.Audio.LastError);
        link.Receive("call_event", """{"eventId":"1","direction":"incoming","state":"idle"}""");
        Assert.False(calls.Audio.InCall);
    }

    [Fact]
    public async Task Dial_RejectsBadNumberAndNeedsAdb()
    {
        var settings = new InMemorySettingsStore();
        using var calls = new CallRelayService(new AdbService(new AdbLocator(), settings), new FakePhoneLink(), null, settings);
        await calls.DialAsync("123; reboot", hearHere: false);
        Assert.Contains("doesn't look like", calls.Status);
        await calls.DialAsync("0501234567", hearHere: false);
        Assert.Contains("ADB", calls.Status);
    }

    [Fact]
    public async Task Perform_UsesLinkWhenConnected()
    {
        var settings = new InMemorySettingsStore();
        var link = new FakePhoneLink { IsConnected = true };
        using var calls = new CallRelayService(new AdbService(new AdbLocator(), settings), link, null, settings);
        await calls.PerformAsync(CallKeyAction.Decline);
        var sent = Assert.Single(link.Sent);
        Assert.Equal("callControl", sent.Type);
        Assert.Contains("\"decline\"", sent.Json);
    }
}

public class CameraTests
{
    [Fact]
    public void CameraArguments_Defaults()
    {
        var a = new PhoneCameraOptions().Arguments("s");
        Assert.Contains("--video-source=camera", a);
        Assert.Contains("--camera-facing=front", a);
        Assert.Contains("--camera-size=1920x1080", a);
        Assert.Contains("--camera-fps=30", a);
        Assert.Contains("--window-title=H.I.B Camera", a);
        Assert.Contains("--window-borderless", a);
        Assert.Contains("--no-audio", a);
        Assert.DoesNotContain(a, x => x.StartsWith("--orientation", StringComparison.Ordinal));
    }

    [Fact]
    public void CameraArguments_Options()
    {
        var a = new PhoneCameraOptions
        {
            Facing = CameraFacing.Back, Quality = CameraQuality.Ar4x3, Fps = 999, WithMicAudio = true,
            Borderless = false, AlwaysOnTop = true, Rotation = 90,
        }.Arguments("s");
        Assert.Contains("--camera-facing=back", a);
        Assert.Contains("--camera-ar=4:3", a);
        Assert.DoesNotContain(a, x => x.StartsWith("--camera-size", StringComparison.Ordinal));
        Assert.Contains("--camera-fps=30", a);
        Assert.Contains("--audio-source=mic", a);
        Assert.DoesNotContain("--no-audio", a);
        Assert.DoesNotContain("--window-borderless", a);
        Assert.Contains("--always-on-top", a);
        Assert.Contains("--orientation=90", a);

        var b = new PhoneCameraOptions { Quality = CameraQuality.Auto, Rotation = 45 }.Arguments("s");
        Assert.DoesNotContain(b, x => x.StartsWith("--orientation", StringComparison.Ordinal) || x.StartsWith("--camera-size", StringComparison.Ordinal) || x.StartsWith("--camera-ar", StringComparison.Ordinal));
    }

    [Fact]
    public void CameraOptions_RoundTripThroughSettings()
    {
        var settings = new InMemorySettingsStore();
        var cam = new PhoneCameraService(new AdbService(new AdbLocator(), settings), settings);
        cam.Options = cam.Options with { Facing = CameraFacing.Back, Fps = 60 };
        Assert.Equal(CameraFacing.Back, cam.Options.Facing);
        Assert.Equal(60, cam.Options.Fps);
    }

    [Fact]
    public void UsbWebcam_Helpers()
    {
        Assert.True(UsbWebcam.IsUvcSupported("true\n"));
        Assert.False(UsbWebcam.IsUvcSupported(""));
        Assert.False(UsbWebcam.IsUvcSupported("false"));
        Assert.Equal(new[] { "mtp", "adb" }, UsbWebcam.ParseFunctions("mtp,adb\n"));
        Assert.Equal(new[] { "sec_charging" }, UsbWebcam.ParseFunctions("sec_charging\n"));
        Assert.True(UsbWebcam.IsWebcamActive(["uvc"]));
        Assert.Equal("", UsbWebcam.RestoreFunction(["sec_charging"]));
        Assert.Equal("ptp", UsbWebcam.RestoreFunction(["ptp", "adb"]));
        Assert.Equal(new[] { "-s", "s", "shell", "svc", "usb", "setFunctions", "uvc" }, UsbWebcam.SetFunctionsArguments("s", "uvc"));
        Assert.Equal(6, UsbWebcam.SetFunctionsArguments("s", "").Count);
        Assert.Equal(6, UsbWebcam.SetFunctionsArguments("s", "mtp;reboot").Count);
    }
}
