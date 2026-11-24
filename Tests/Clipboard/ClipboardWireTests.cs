using Hydra.Platform;
using Hydra.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using static Tests.Setup.Snapshots;

namespace Tests.Clipboard;

// how a snapshot is put on the wire and read back
[TestFixture]
public class ClipboardWireTests
{
    private static readonly NullLogger Log = NullLogger.Instance;

    [Test]
    public void PrimaryRepeatingTheText_IsSentOnce()
    {
        var push = ClipboardWire.ToPush(Snapshot("copied", "copied", null));
        var response = ClipboardWire.AnswerPull(Snapshot("copied", "copied", null), new ClipboardPullMessage(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.PrimaryText, Is.Null);
            Assert.That(push.PrimaryIsText, Is.True);
            Assert.That(ClipboardWire.From(push, Log, "push from peer").Selection, Is.EqualTo("copied"));
            Assert.That(response.PrimaryText, Is.Null);
            Assert.That(ClipboardWire.From(response, Log, "pull response from peer").Selection, Is.EqualTo("copied"));
        }
    }

    [Test]
    public void PrimaryOfItsOwn_IsSentAsIs()
    {
        var push = ClipboardWire.ToPush(Snapshot("copied", "selected", null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.PrimaryText, Is.EqualTo("selected"));
            Assert.That(push.PrimaryIsText, Is.Null);
        }
    }

    [Test]
    public void ConcealedCopy_CrossesConcealed_BothWays()
    {
        var concealed = Snapshot("hunter2", null, null, Concealed: true);
        var push = RoundTrip(MessageKind.ClipboardPush, ClipboardWire.ToPush(concealed));
        var answer = RoundTrip(MessageKind.ClipboardPullResponse, ClipboardWire.AnswerPull(concealed, new ClipboardPullMessage(1)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.Concealed, Is.True);
            Assert.That(ClipboardWire.From(push, Log, "push from peer").Clipboard.Secret, Is.True);
            Assert.That(answer.Concealed, Is.True);
            Assert.That(ClipboardWire.From(answer, Log, "pull response from peer").Clipboard.Secret, Is.True);
        }
    }

    [Test]
    public void PlainCopy_CarriesNoConcealedFlag()
    {
        var push = ClipboardWire.ToPush(Snapshot("plain", null, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(push.Concealed, Is.Null);
            Assert.That(ClipboardWire.From(push, Log, "push from peer").Clipboard.Secret, Is.False);
        }
    }

    [Test]
    public void ConcealedCopy_IsDescribedWithoutItsText()
    {
        var described = ClipboardWire.Describe(ClipboardWire.ToPush(Snapshot("hunter2", null, null, Concealed: true)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(described, Does.Contain("concealed"));
            Assert.That(described, Does.Not.Contain("hunter2"));
        }
    }

    [Test]
    public void ClearedFlags_SurviveTheWire()
    {
        var answer = RoundTrip(MessageKind.ClipboardPullResponse, new ClipboardPullResponseMessage(1, null, Cleared: true));
        var hash = RoundTrip(MessageKind.ClipboardHash, new ClipboardHashMessage(null, Cleared: true));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(answer.Cleared, Is.True);
            Assert.That(hash.Cleared, Is.True);
        }
    }

    private static T RoundTrip<T>(MessageKind kind, T message) => MessageSerializer.Decode(MessageSerializer.Encode(kind, message)).Deserialize<T>();
}
