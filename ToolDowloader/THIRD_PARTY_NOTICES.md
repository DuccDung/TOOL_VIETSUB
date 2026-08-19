# Third-party notices

This application uses .NET, Entity Framework Core, SQLite, Serilog, and xUnit under their respective licenses.

FFmpeg is not included in source control. By default, the application can download the pinned FFmpeg
9.0.1 Essentials Windows package published by gyan.dev and linked from ffmpeg.org, using the publisher's
GitHub release mirror. That build is identified
by its publisher as GPLv3. The archive is retained only as an application-managed local tool and its
SHA-256 is verified before extraction. Review the package's included license before distributing it or
shipping it inside an installer.

If an FFmpeg binary is distributed with the application, the distributor must include the applicable
FFmpeg build license, notices, and source-code offer where required.
