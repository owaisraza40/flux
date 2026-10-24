# Security

Do not include private download URLs, cookies, passwords, or access tokens in public issue reports. Include the application version, a description, and a public or local reproduction instead.

Flux validates HTTPS certificates using the Windows/.NET trust store. It accepts HTTP and HTTPS URLs; browser integration does not execute arbitrary local commands or accept file URLs. Downloads are saved to the location selected by the user. Treat downloaded executables with the same care as files downloaded in your browser.

The local application state includes download URLs and user-supplied HTTP headers. Do not share your application data directory publicly. See the user guide for its location.

For a suspected vulnerability, use the repository owner's contact options rather than publicly posting exploitable details or secrets. This early project does not yet have an independently audited security guarantee.
