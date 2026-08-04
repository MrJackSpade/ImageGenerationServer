using System.Data.Common;

namespace ImageGen.Infrastructure.Database;

/// <summary>
/// Creates and opens connections to the ImageGen database.
///
/// <para>Returns <see cref="DbConnection"/>, not a provider type. A concrete <c>SqlConnection</c> would put
/// "this app runs on SQL Server" into the signature every repository is written against — returning the base type
/// is what makes a second provider possible.</para>
///
/// <para>An implementation is responsible for returning a connection that is <b>ready to run the repositories'
/// SQL as written</b>. For SQLite that means more than opening a file: see <see cref="SqliteConnectionFactory"/>,
/// which has to attach the database under the <c>dbo</c> schema name and turn foreign keys on.</para>
/// </summary>
public interface IDbConnectionFactory
{
    /// <summary>Open a connection, configured so the repositories' SQL runs against it unmodified.</summary>
    Task<DbConnection> OpenAsync(CancellationToken ct);
}
