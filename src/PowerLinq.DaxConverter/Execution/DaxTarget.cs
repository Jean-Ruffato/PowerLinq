namespace PowerLinq.DaxConverter.Execution;

/// <summary>
/// A query's target: the workspace and the semantic model the DAX is executed against.
/// </summary>
/// <param name="Workspace">
/// The workspace's XMLA endpoint, in the form the server expects — for example
/// <c>powerbi://api.powerbi.com/v1.0/myorg/MyWorkspace</c>.
/// </param>
/// <param name="Dataset">Name of the dataset (semantic model) published in the workspace.</param>
/// <remarks>
/// <para>
/// It exists for the multi-tenant case, which is the common one in a BI SaaS product: the same
/// dataset is published in one workspace <b>per customer</b>, and the target is only known partway
/// through the request, after the tenant has been resolved. Without it, the target came solely
/// from configuration and applied to the whole process — one application, one model.
/// </para>
/// <para>
/// <paramref name="Workspace"/> is the complete endpoint, not a name to be composed into a URI.
/// Composing it would depend on the cloud (commercial, government, sovereign) and on a tenant
/// convention, and getting that silently wrong would produce an authentication failure that is
/// hard to read. Whoever resolves the tenant already has the endpoint; passing it whole is
/// explicit and has no special cases.
/// </para>
/// <para>
/// It is a <c>record</c> because this is what the pool uses to group connections: two equal
/// targets have to be equal by value, otherwise every query would open its own connection.
/// </para>
/// </remarks>
public sealed record DaxTarget(string Workspace, string Dataset);
