namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Endpoints related to one document, so a client never has to build paths by hand.
/// </summary>
/// <param name="Self">Consolidated detail.</param>
/// <param name="Status">Lightweight status for polling.</param>
/// <param name="Content">Original file, inline by default.</param>
/// <param name="Download">Original file as an attachment.</param>
public sealed record DocumentLinks(string Self, string Status, string Content, string Download);
