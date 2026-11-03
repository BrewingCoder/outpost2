namespace OutPost2.Core.Files;

/// <summary>
/// Too many unspent one-use links are outstanding for this user.
/// <para>
/// Its own type so the surface can speak this message and only this message. It used to be
/// an <see cref="InvalidOperationException"/>, which meant the tool's catch also swallowed
/// any such exception from EF, Npgsql or configuration and read its text out — exactly the
/// leak OP2-259 was about, arriving by a different door.
/// </para>
/// </summary>
public sealed class TooManyTicketsException(string message) : Exception(message);
