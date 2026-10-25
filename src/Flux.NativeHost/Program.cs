using Flux.App.Integration;

// Browser native messaging is binary framed stdin/stdout. Never write startup banners to stdout.
try { return await NativeBridge.RunHostAsync(); }
catch { return 1; }
