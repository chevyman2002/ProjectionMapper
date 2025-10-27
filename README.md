# ProjectionMapper

Open source projection mapping software, inspired by the MapMap project.

I attempted to fork MapMap and modernize it, while fixing a bug causing a crash in Win11, as well as tweak features I wanted. However, the various Qt libraries have changed over the years and I rapidly grew annoyed by endless battles between compilation errors and behavioral issues.

This project is a from-scratch build in C# .NET 9. I considered making it in .NET MAUI for cross-platform support, but I'm realistically just trying to get something simple to use for my wedding in <6 months, and being cheap about it (not wanting to buy a license for anything, if possible).

The program icon was generated in Gemini. The project uses FFmpeg for video decoding/processing.

The entire program has been 100% built using CoPilot+ AI agents. Any changes I've actually made to code was to either add or remove a UI element that the AI agents weren't doing and it was simpler to just manually do it. There are for sure bugs in the current code and the audio doesn't work whatsoever (video starts glitching uncontrollably and whatnot). That's something I'll have AI fix, but it's not critical.

The program currently compiles and runs, allowing you to send video output to two different connected screens/projectors. I've been testing with a physical connection via HDMI _and_ wirelessly (WiFi) to two very cheap projectors, and it works fine in short tests.
