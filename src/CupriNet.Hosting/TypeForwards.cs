using System.Runtime.CompilerServices;

// ShrineSession moved to CupriNet.Shrine so a Pilgrim can visit a site without the node-hosting stack (and its
// sockets) coming along. It kept its namespace, and this forward keeps assemblies compiled against CupriNet.Hosting
// 0.3.2 and earlier resolving it here.
[assembly: TypeForwardedTo(typeof(CupriNet.Hosting.ShrineSession))]
