using System.Linq.Expressions;
using PowerLinq.DaxConverter.Queries;

namespace PowerLinq.DaxConverter.Linq;

/// <summary>
/// The pipeline behind one of this library's <see cref="IQueryable"/> instances, without the type
/// parameter.
/// </summary>
/// <remarks>
/// The provider receives the expression tree already built and has to recover the source's state
/// from a <see cref="ConstantExpression"/>, where the element type is not known statically. It is
/// the same reason <c>DaxStageTranslator</c> is not generic.
/// </remarks>
internal interface IDaxQueryable
{
    /// <summary>The query this expression represents, already translated.</summary>
    DaxPipeline Pipeline { get; }

    /// <summary>The provider that composed it, owner of the executor and of the message language.</summary>
    DaxQueryProvider DaxProvider { get; }
}
