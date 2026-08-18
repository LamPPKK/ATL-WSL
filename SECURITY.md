# Security policy

Report suspected vulnerabilities privately through GitHub's security advisory feature for this repository. Do not attach proprietary APKs, tokens or personal app data.

ATL-WSL treats APKs as untrusted input and launches each one through a mandatory bubblewrap filesystem sandbox inside WSL. The sandbox omits Windows drives, WSL interop, the user's Linux home and other applications' data; it exposes system files and WSLg read-only and gives the selected app one writable data directory. It intentionally shares networking. This reduces accidental or malicious host access, but ATL and WSL are still not a security boundary equivalent to a complete Android sandbox or separate physical device. Install only APKs you trust and keep Windows and WSL current.

Release installation is fail-closed: artifacts are accepted only when their size and SHA-256 match the HTTPS release manifest. The installer refuses name and directory collisions and never unregisters a distribution during rollback. SHA-256 protects integrity against accidental or mismatched downloads; it is not a substitute for verifying the GitHub release publisher.

Diagnostic ZIPs contain ATL-WSL version and health details, app display names, source filenames, hashes, configuration metadata and recent logs. They exclude APK archives and app data, but logs can still contain information emitted by an application. Review an archive before sharing it.
