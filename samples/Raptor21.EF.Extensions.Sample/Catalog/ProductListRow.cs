using Raptor21.EF.Extensions.Sample.Data;
using Raptor21.EF.Extensions.StoredProcedures.Generated;

namespace Raptor21.EF.Extensions.Sample.Catalog;

/// <summary>
/// The same kind of row as <see cref="ProductRow"/>, with the members taken from the EF model instead of
/// being written out here. The generator writes the primary constructor as well as
/// <c>FromDataRecord</c>, so this declaration is the whole file.
/// </summary>
/// <remarks>
/// <para>
/// Nothing parses the procedure's <c>SELECT</c>. The developer names the entity; the generator reads the
/// checked-in <c>CatalogDbContextModelSnapshot</c> as ordinary C# syntax in this same compilation - no
/// EF types, no database connection, no reflection - and takes four member names, four .NET types, four
/// column names and four nullability decisions from it. That is four facts times four members that stop
/// being a second copy of the model, quietly wrong the day a column changes type.
/// </para>
/// <para>
/// Why this form rather than <see cref="ProductRow"/>'s: the row is exactly a shape the model already
/// describes, so writing it twice is a copy that only ever drifts. Why <see cref="ProductRow"/> rather
/// than this form: see the comment on that type - a projection the model does not describe, a
/// <c>[SqlColumn]</c> rename, a member order chosen to match an existing <c>SELECT</c>, or a value
/// converter the generator cannot run all need the members spelled out by hand, and the positional
/// record keeps working forever.
/// </para>
/// <para>
/// The three things worth knowing before writing the procedure, all repeated on the attribute itself:
/// members come out in the model's order (primary key first, then alphabetical) and the <c>SELECT</c>
/// must match it because result columns are validated positionally; the member type is the model's
/// STORED type rather than the entity class's, so a value-converted or enum property is refused rather
/// than silently emitted under the other's name; and shadow properties are skipped in silence, so a
/// shadow foreign key the model knows about must not appear in the <c>SELECT</c> either.
/// </para>
/// </remarks>
[SqlRow(Entity = typeof(ProductListResult))]
public partial record ProductListRow;
