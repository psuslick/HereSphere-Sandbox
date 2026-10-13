@{
    # Application source is cloned fresh into C:\App on every Sandbox launch.
    # Change this to the repository you control.
    RepositoryUrl = 'https://github.com/REPLACE_ME/heresphere-sandbox-server.git'

    # Branch or tag to clone. Keep 'main' if you want each launch to run the
    # current main branch. Use a release tag when you want deterministic code.
    Ref = 'main'

    # Path inside the cloned repository containing the C# server source.
    EntrySource = 'src\HereSphereSandboxServer.cs'

    # Remote submodules are intentionally disabled by default. The initial
    # server has no external source dependencies.
    AllowSubmodules = $false
}
