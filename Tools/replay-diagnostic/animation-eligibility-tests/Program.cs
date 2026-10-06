using System.Collections;
using System.Reflection;
using Content.Replay.Diagnostic;
using Robust.Client.ComponentTrees;
using Robust.Client.GameObjects;
using Robust.Client.GameStates;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Log;
using Robust.Shared.Maths;
using Robust.Shared.Utility;

// Exercise the compiled production queue and native setters on small objects.
// No engine startup, prototype loading, replay, capture or native renderer.
var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
void Inject(object target, string name, object value) => target.GetType().GetField(name, Private)!.SetValue(target, value);
object? Call(object target, string name, params object?[] values)
{
    try { return target.GetType().GetMethod(name, Private)!.Invoke(target, values); }
    catch (TargetInvocationException e) when (e.InnerException != null)
    { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
}

var system = new SpriteSystem();
var tree = new SpriteTreeSystem();
var resources = new ResourceCache();
var fallback = new RSI(new Vector2i(32, 32), new ResPath("/Textures/error.rsi"));
RSI.State State(RSI rsi, string name, int count) => new(new Vector2i(32, 32), rsi, new RSI.StateId(name),
    RsiDirectionType.Dir1, Enumerable.Repeat(0.1f, count).ToArray(), [Array.Empty<AtlasTexture>()]);
fallback.AddState(State(fallback, "error", 4));
var fallbackResource = new RSIResource();
typeof(RSIResource).GetProperty(nameof(RSIResource.RSI))!.SetValue(fallbackResource, fallback);
((IDictionary) typeof(ResourceCache).GetField("_fallbacks", Private)!.GetValue(resources)!)[typeof(RSIResource)] = fallbackResource;
Inject(system, "_tree", tree);
Inject(system, "_resourceCache", resources);

var uid = new EntityUid(100);
SpriteComponent Sprite(EntityUid owner, RSI? rsi, string state = "animated", bool visible = true)
{
    var sprite = new SpriteComponent { Owner = owner };
    Inject(sprite, "_sys", system);
    Inject(sprite, "_treeSys", tree);
    Inject(sprite, "resourceCache", resources);
    sprite._baseRsi = rsi; // fixture construction only; mutations below use production APIs
    sprite.Layers.Add(new SpriteComponent.Layer((owner, sprite), 0) { StateId = new RSI.StateId(state), _visible = visible });
    return sprite;
}
RSI Rsi(int count)
{
    var rsi = new RSI(new Vector2i(32, 32), new ResPath("/Textures/test.rsi"));
    rsi.AddState(State(rsi, "animated", count));
    return rsi;
}

var runner = new CaptureRunner();
var owners = (IDictionary) typeof(CaptureRunner).GetField("_presentationOwners", Private)!.GetValue(runner)!;
Call(runner, "TrackPresentationOwner", 100, uid);
var ownerRecord = owners[100]!;
var sync = new Dictionary<EntityUid, IComponent>();
var syncQuery = (EntityQuery<SyncSpriteComponent>) Activator.CreateInstance(typeof(EntityQuery<SyncSpriteComponent>),
    Private, null, [null, sync], null)!;
var queued = (HashSet<EntityUid>) typeof(SpriteSystem).GetField("_queuedFrameUpdate", Private)!.GetValue(system)!;
var inert = (Queue<SpriteComponent>) typeof(SpriteSystem).GetField("_inertUpdateQueue", Private)!.GetValue(system)!;
var options = typeof(CaptureRunner).Assembly.GetType("Content.Replay.Diagnostic.Program")!;
var strategy = options.GetField("OrdinaryAnimationEligibility", BindingFlags.Static | BindingFlags.Public)!;
void Mode(string mode) => strategy.SetValue(null, Enum.Parse(strategy.FieldType, mode));
long Visits() => (long) typeof(CaptureRunner).GetField("_queueLayerVisits", Private)!.GetValue(runner)!;
object EligibilityOwner(SpriteComponent sprite) => Activator.CreateInstance(ownerRecord.GetType(),
    Private | BindingFlags.Public, null, [sprite.Owner], null)!;
bool Queue(SpriteComponent sprite, object? eligibilityOwner = null)
{
    queued.Clear();
    Call(runner, "QueueOrdinarySprite", sprite.Owner, sprite, system, syncQuery, eligibilityOwner ?? ownerRecord);
    return queued.Contains(sprite.Owner);
}
void Parity(SpriteComponent sprite, bool expected, string message, object? eligibilityOwner = null)
{
    Mode("Reference"); var reference = Queue(sprite, eligibilityOwner);
    Mode("Cached"); var cached = Queue(sprite, eligibilityOwner);
    Check(reference == expected && cached == reference, message);
}

Check(strategy.GetValue(null)!.ToString() == "Cached" && AnimationEligibility.Parse(null) == AnimationEligibilityStrategy.Cached
    && AnimationEligibility.Parse("reference") == AnimationEligibilityStrategy.Reference
    && AnimationEligibility.Parse("cached") == AnimationEligibilityStrategy.Cached,
    "Cached must be the default, with both explicit strategies available.");
try { AnimationEligibility.Parse("active-only"); throw new InvalidOperationException("Unknown strategy accepted."); }
catch (ArgumentException) { checks++; }

var animated = Rsi(3);
var sprite = Sprite(uid, animated);
Parity(sprite, true, "Resolved visible auto multiframe selection differs.");
var visits = Visits();
Check(Queue(sprite) && Visits() == visits, "Stable cached owner rescanned its layers.");
Mode("Reference"); Queue(sprite); Queue(sprite);
Check(Visits() == visits + 2, "Reference fallback must scan every pass.");
Mode("Cached");

// The native flag remains set across both notifications. Simulates another
// mutation after pre-frame classification, before native inert queue draining.
system.QueueUpdateIsInert((uid, sprite));
var revision = sprite.ReplayAnimationEligibilityRevision;
var queuedInert = inert.Count;
Queue(sprite);
system.QueueUpdateIsInert((uid, sprite));
Check(sprite.ReplayAnimationEligibilityRevision > revision && inert.Count == queuedInert,
    "Coalesced inert call lost an eligibility invalidation.");
visits = Visits(); Queue(sprite);
Check(Visits() > visits, "Second coalesced invalidation reused an earlier selection.");

system.SetBaseRsi((uid, sprite), null);
Parity(sprite, false, "Null base RSI must invalidate despite native animated fallback.");
system.SetBaseRsi((uid, sprite), Rsi(1));
Parity(sprite, false, "Static replacement base RSI stayed active.");
system.SetBaseRsi((uid, sprite), animated);
Parity(sprite, true, "Multiframe replacement base RSI stayed inactive.");

sprite.Layers[0].Visible = false;
Check(!Queue(sprite), "Hidden layer was queued.");
sprite.Layers[0].Visible = true;
Check(queued.Count == 0, "A FrameUpdate-time mutation retroactively changed prior selection.");
Check(Queue(sprite), "Hidden-to-visible activation was not selected on next pass.");
sprite.Layers[0].AutoAnimated = false;
Parity(sprite, false, "Legacy auto-animation disable failed.");
system.LayerSetAutoAnimated(sprite.Layers[0], true);
Parity(sprite, true, "System auto-animation reactivation failed.");
system.LayerSetRsi(sprite.Layers[0], Rsi(1));
Parity(sprite, false, "Layer RSI replacement failed.");
system.LayerSetRsi(sprite.Layers[0], animated);
system.LayerSetRsiState(sprite.Layers[0], RSI.StateId.Invalid);
Parity(sprite, false, "Invalid state used native fallback.");
system.LayerSetRsiState(sprite.Layers[0], "animated");
Parity(sprite, true, "State reactivation failed.");

// Native legacy properties condition their inert notification on Sys availability.
// Eligibility invalidation must still cover these mutations before engine startup.
var beforeStartup = Sprite(new EntityUid(102), animated);
Inject(beforeStartup, "_sys", null!);
Inject(beforeStartup, "entities", new ClientEntityManager());
revision = beforeStartup.ReplayAnimationEligibilityRevision;
beforeStartup.Layers[0].Visible = false;
Check(beforeStartup.ReplayAnimationEligibilityRevision > revision, "Pre-start legacy visibility was untracked.");
revision = beforeStartup.ReplayAnimationEligibilityRevision;
beforeStartup.Layers[0].AutoAnimated = false;
Check(beforeStartup.ReplayAnimationEligibilityRevision > revision, "Pre-start legacy auto-animation was untracked.");
revision = beforeStartup.ReplayAnimationEligibilityRevision;
beforeStartup.Layers[0].RSI = animated;
Check(beforeStartup.ReplayAnimationEligibilityRevision > revision, "Pre-start legacy RSI replacement was untracked.");
revision = beforeStartup.ReplayAnimationEligibilityRevision;
beforeStartup.Layers[0].State = RSI.StateId.Invalid;
Check(beforeStartup.ReplayAnimationEligibilityRevision > revision, "Pre-start legacy state replacement was untracked.");

animated.AddState(State(animated, "animated", 1));
Parity(sprite, false, "Same-resource state replacement failed to invalidate.");
animated.RemoveState("animated");
Parity(sprite, false, "Same-resource state removal used fallback.");
animated.AddState(State(animated, "animated", 3));
Parity(sprite, true, "Same-resource state restoration failed.");
var epoch = RSI.ReplayAnimationStateEpoch;
Parallel.For(0, 64, i => { var rsi = Rsi(2); rsi.RemoveState("animated"); });
Check(RSI.ReplayAnimationStateEpoch == epoch + 128, "Parallel resource epoch increments were lost.");

var blank = system.AddBlankLayer((uid, sprite), 0);
Parity(sprite, true, "Blank insertion/remapping changed selection.");
revision = sprite.ReplayAnimationEligibilityRevision;
system.RemoveLayer((uid, sprite), blank.Index, out _);
Check(sprite.ReplayAnimationEligibilityRevision > revision, "Layer removal was not tracked.");

var empty = new SpriteComponent { Owner = new EntityUid(101) };
system.CopySprite((empty.Owner, empty), (uid, sprite));
Parity(sprite, false, "Empty CopySprite retained active membership.");
sprite = Sprite(uid, animated); Queue(sprite);
var hiddenCopy = Sprite(empty.Owner, animated, visible: false);
system.CopySprite((empty.Owner, hiddenCopy), (uid, sprite));
Parity(sprite, false, "Nonempty CopySprite missed final copied visibility.");
sprite = Sprite(uid, animated); Queue(sprite);
sprite.Layers[0].Loop = false;
sprite.Layers[0].AnimationTimeLeft = -1f;
sprite.Layers[0].AdvanceFrameAnimation(animated["animated"]);
Parity(sprite, false, "Native non-loop completion missed auto-animation invalidation.");
sprite = Sprite(uid, animated); Queue(sprite);
system.LayerSetTexture(sprite.Layers[0], (Texture?) null);
Parity(sprite, false, "Texture replacement missed state invalidation.");
sprite = Sprite(uid, animated);
Check(Queue(sprite), "Replacement sprite fixture is not active.");
revision = sprite.ReplayAnimationEligibilityRevision;
sprite.layerDatums.Add(new PrototypeLayerData());
resources.LoadBaseRsi(uid, sprite);
Check(sprite.ReplayAnimationEligibilityRevision > revision, "Resource bulk reconstruction was not tracked.");
Parity(sprite, false, "Resource bulk blank replacement retained active membership.");
sprite = Sprite(uid, animated);
Queue(sprite);
sprite.layerDatums.Add(new PrototypeLayerData());
Call(system, "LoadLayers", new Entity<SpriteComponent>(uid, sprite));
Parity(sprite, false, "System bulk blank replacement retained active membership.");

sprite = Sprite(uid, animated); Queue(sprite);
var replacement = Sprite(uid, animated, visible: false);
Check(replacement.ReplayAnimationIdentity != sprite.ReplayAnimationIdentity
    && replacement.ReplayAnimationEligibilityRevision == sprite.ReplayAnimationEligibilityRevision,
    "Replacement fixture must distinguish equal revisions by identity.");
Parity(replacement, false, "Same-UID component replacement reused old membership.");
sprite = Sprite(uid, animated); Queue(sprite);
sync[uid] = new SyncSpriteComponent { Owner = uid };
Check(!Queue(sprite), "Live SyncSprite addition was ignored.");
sync.Remove(uid);
visits = Visits();
Check(Queue(sprite) && Visits() > visits, "Live SyncSprite removal reused a suppressed membership.");
visits = Visits(); Call(runner, "ResetAnimationEligibility"); Queue(sprite);
Check(Visits() > visits, "World/checkpoint reset retained cached eligibility.");

// AllLayers no longer permits same-count replacement or collection mutation.
var viewSprite = Sprite(new EntityUid(110), animated);
var view = viewSprite.AllLayers;
var originalLayer = viewSprite.Layers[0];
Check(view is not List<SpriteComponent.Layer>, "Public view still exposes the mutable backing list.");
var readOnly = (IList<SpriteComponent.Layer>) view;
Check(readOnly.IsReadOnly, "The public collection accepts structural mutation.");
try { readOnly[0] = new SpriteComponent.Layer(); throw new InvalidOperationException("Same-count replacement accepted."); }
catch (NotSupportedException) { checks++; }
try { readOnly.Add(new SpriteComponent.Layer()); throw new InvalidOperationException("Public collection insertion accepted."); }
catch (NotSupportedException) { checks++; }
Check(readOnly.Count == 1 && ReferenceEquals(readOnly[0], originalLayer), "Rejected writes altered native membership.");
Check(ReferenceEquals(viewSprite.GetReplayAnimationLayers(1)[0], originalLayer), "Read-only traversal changed layer identity.");
var allocatedBeforeReads = GC.GetAllocatedBytesForCurrentThread();
var sameView = true;
var traversed = 0;
for (var i = 0; i < 1000; i++)
{
    sameView &= ReferenceEquals(view, viewSprite.AllLayers);
    traversed += viewSprite.GetReplayAnimationLayers(1).Length;
}
Check(sameView && traversed == 1000 && GC.GetAllocatedBytesForCurrentThread() == allocatedBeforeReads,
    "Repeated public/span reads allocated or replaced the wrapper.");
var viewBlank = system.AddBlankLayer((viewSprite.Owner, viewSprite));
Check(readOnly.Count == 2 && ReferenceEquals(readOnly[1], viewBlank), "Existing view stopped observing native list edits.");
var emptyViewSource = new SpriteComponent { Owner = new EntityUid(111) };
system.CopySprite((emptyViewSource.Owner, emptyViewSource), (viewSprite.Owner, viewSprite));
Check(!ReferenceEquals(view, viewSprite.AllLayers) && !viewSprite.AllLayers.Any()
    && readOnly.Count == 2 && ReferenceEquals(readOnly[0], originalLayer),
    "CopySprite changed captured-view semantics or left a stale current view.");
Check(viewSprite.GetReplayAnimationLayers(0).IsEmpty, "An empty sprite did not fit the zero-layer traversal budget.");
try { viewSprite.GetReplayAnimationLayers(-1); throw new InvalidOperationException("Negative traversal budget accepted."); }
catch (ArgumentOutOfRangeException) { checks++; }

// AddLayer preserves native aliasing, but both owners permanently full-scan.
var aliasA = Sprite(new EntityUid(112), animated, visible: false);
var aliasB = Sprite(new EntityUid(113), animated, visible: false);
var entryA = EligibilityOwner(aliasA);
var entryB = EligibilityOwner(aliasB);
Check(!Queue(aliasA, entryA), "Alias source fixture must initially be ineligible.");
var shared = aliasA.Layers[0];
var aliasIndex = system.AddLayer((aliasB.Owner, aliasB), shared);
Check(aliasA.Layers.Count == 1 && aliasB.Layers.Count == 2 && aliasIndex == 1
    && ReferenceEquals(aliasA.Layers[0], aliasB.Layers[aliasIndex]) && ReferenceEquals(shared.Owner.Comp, aliasB),
    "The fix changed native alias insertion or reassignment semantics.");
Check(!aliasA.ReplayAnimationEligibilityCacheSafe && !aliasB.ReplayAnimationEligibilityCacheSafe,
    "An affected alias owner can still cache eligibility.");
Check(!Queue(aliasA, entryA) && !Queue(aliasB, entryB), "Hidden alias fixture unexpectedly animated.");
var oldOwnerRevision = aliasA.ReplayAnimationEligibilityRevision;
system.LayerSetVisible(shared, true);
Check(aliasA.ReplayAnimationEligibilityRevision == oldOwnerRevision,
    "The regression must exercise the native missing old-owner notification.");
Parity(aliasA, true, "Old alias owner reused its stale hidden classification.", entryA);
Parity(aliasB, true, "New alias owner failed reference selection.", entryB);
visits = Visits(); Queue(aliasA, entryA); Queue(aliasA, entryA);
Check(Visits() == visits + 2, "Stable alias owner resumed caching.");
shared.AutoAnimated = false;
Parity(aliasA, false, "Legacy shared-layer mutation left old owner active.", entryA);
shared.AutoAnimated = true;
system.LayerSetRsi(shared, Rsi(1));
Parity(aliasA, false, "Shared RSI replacement left old owner active.", entryA);
system.LayerSetRsi(shared, animated);
Check(system.RemoveLayer((aliasB.Owner, aliasB), aliasIndex, out var removedShared)
    && ReferenceEquals(removedShared, shared) && shared.Owner.Comp == null
    && ReferenceEquals(aliasA.Layers[0], shared), "Native alias removal semantics changed.");
var aliasC = Sprite(new EntityUid(114), animated, visible: false);
system.AddLayer((aliasC.Owner, aliasC), shared);
Check(!aliasC.ReplayAnimationEligibilityCacheSafe, "Alias history was lost after removal and re-addition.");
var entryC = EligibilityOwner(aliasC);
Parity(aliasC, true, "Re-added shared layer differs from reference.", entryC);
Call(runner, "ResetAnimationEligibility");
system.CopySprite((emptyViewSource.Owner, emptyViewSource), (aliasA.Owner, aliasA));
Check(!aliasA.ReplayAnimationEligibilityCacheSafe && !aliasB.ReplayAnimationEligibilityCacheSafe
    && !aliasC.ReplayAnimationEligibilityCacheSafe, "Reset or reconstruction re-enabled an affected instance.");
var freshCopy = Sprite(new EntityUid(115), animated);
system.CopySprite((aliasC.Owner, aliasC), (freshCopy.Owner, freshCopy));
Check(freshCopy.ReplayAnimationEligibilityCacheSafe && !freshCopy.Layers[1].ReplayAnimationWasShared
    && !ReferenceEquals(freshCopy.Layers[1], shared), "Independent clones inherited alias fallback.");

// Failed AddLayer still clears Owner in native code while the prior list retains it.
var missingManager = new ClientEntityManager();
missingManager.MetaQuery = (EntityQuery<MetaDataComponent>) Activator.CreateInstance(typeof(EntityQuery<MetaDataComponent>),
    Private, null, [missingManager, new Dictionary<EntityUid, IComponent>()], null)!;
using var missingLogs = new LogManager();
typeof(EntityManager).GetField("ResolveSawmill", Private)!.SetValue(missingManager, missingLogs.GetSawmill("resolve"));
Inject(system, "_query", Activator.CreateInstance(typeof(EntityQuery<SpriteComponent>),
    Private, null, [missingManager, new Dictionary<EntityUid, IComponent>()], null)!);
var failedOwner = Sprite(new EntityUid(116), animated);
var failedLayer = failedOwner.Layers[0];
Check(system.AddLayer((new EntityUid(117), (SpriteComponent?) null), failedLayer) == -1
    && failedLayer.Owner.Comp == null && failedLayer.Index == -1
    && ReferenceEquals(failedOwner.Layers[0], failedLayer) && !failedOwner.ReplayAnimationEligibilityCacheSafe,
    "Failed reassignment changed semantics or left the prior owner caching.");

// Inspection remains on its engine owner thread. This does not guard native writers.
var ownerThread = Environment.CurrentManagedThreadId;
AnimationEligibility.RequireOwnerThread(ownerThread);
Check(Task.Run(() =>
{
    try { AnimationEligibility.RequireOwnerThread(ownerThread); return false; }
    catch (InvalidOperationException) { return true; }
}).GetAwaiter().GetResult(), "Off-thread inspection was accepted.");

// Count enforcement precedes cache lookup, even for an already eligible owner.
for (var i = sprite.Layers.Count; i < 257; i++)
    sprite.Layers.Add(new SpriteComponent.Layer((uid, sprite), i));
try { Queue(sprite); throw new InvalidOperationException("Cached hit skipped layer cap."); }
catch (InvalidDataException) { checks++; }
Call(runner, "RemoveProjection", 100, new List<int>(), new List<object>());
Check(owners.Count == 0, "Deleted projection retained an eligibility record.");

var stamp = new AnimationEligibilityStamp(1, 1, 1, 1);
Check(!AnimationEligibilityEntry.AfterScan(stamp, stamp with { ResourceEpoch = 2 }, true).TryGet(stamp, out _),
    "A resource mutation during scanning published a cache entry.");
Check(!AnimationEligibilityEntry.AfterScan(stamp, stamp with { Revision = 2 }, true).TryGet(stamp, out _),
    "An owner mutation during scanning published a cache entry.");
Mode("Reference");
Console.WriteLine($"Animation eligibility regressions passed: {checks}. No engine or replay started.");
