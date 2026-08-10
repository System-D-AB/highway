namespace Highway.Abstractions;

/// <summary>
/// Declares that this assembly's handlers are meant to be hosted by any process that
/// references it (feature 024).
///
/// <para>Contract discovery never needs this — contracts are found across the whole reference
/// closure in every <c>HostingMode</c>. This attribute answers the other question: whether the
/// assembly's <c>AsyncService&lt;,&gt;</c>, <c>IProcess&lt;T&gt;</c> and
/// <c>ISubscribe&lt;T&gt;</c> implementations run in a referencing process. Under
/// <c>HostingMode.Declared</c> or <c>ExplicitOnly</c>, an assembly without this attribute (and
/// not passed to <c>HostAssembly(...)</c>) contributes contracts only; its handlers are
/// skipped and reported at startup.</para>
///
/// <para>The attribute is the library author's consent; <c>HostAssembly(...)</c> at the
/// composition root is the application author's. Either is sufficient, both together are
/// idempotent.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class HighwayHostModuleAttribute : Attribute;
