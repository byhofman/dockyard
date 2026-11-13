namespace Dockyard.Core;

// Where the CA's private key lives. Read returns null only when there is none; a store that cannot answer right now
// throws, because treating that as "none" would make a new CA and ask for trust all over again.
interface ICaKeyStore
{
    Task<string?> Read();
    Task Write(string keyPem);
    Task Delete();
}
