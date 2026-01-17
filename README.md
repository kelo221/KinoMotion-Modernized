# KinoMotion (Modernized)

**KinoMotion** is a high-performance motion blur post-processing effect for Unity. This version is a modernized fork of [Keijiro Takahashi's original KinoMotion](https://github.com/keijiro/KinoMotion), updated for modern Unity workflows and standards.

![gif](https://i.imgur.com/UkJvWnc.gif)
![gif](https://i.imgur.com/tJioLuY.gif)


## What's Modernized?
This fork has been refactored to support professional project standards and the latest Unity engine releases:

- **Unity 6.3 LTS Ready**: Fully tested and optimized for the Unity 6 (6000.3.x) Long Term Support release.
- **UPM Support**: Distributed as a proper Unity Package. Install directly via Git URL.
- **Assembly Definitions (`.asmdef`)**: Separated `Runtime` and `Editor` assemblies for faster compilation and clean dependency management.
- **Modern Layout**: Follows the official `Runtime/Editor` folder structure for clean project organization.
- **HLSL Transition**: Core shaders have been updated/included with HLSL support for improved compatibility with modern render pipelines.

## System Requirements
- **Unity Version**: 2019.4 LTS up to **Unity 6.3 LTS**.
- **Platform**: Desktop and Console (requires Motion Vector texture support).
- **Format**: Requires `RGHalf` texture format support (not available on all mobile devices).

## Installation

### Via Git URL
1. In Unity, open **Window > Package Manager**.
2. Click the **+** (plus) button and select **Add package from git URL...**
3. Enter the following URL:
   ```text
   https://github.com/kelo221/KinoMotion-Modernized.git
   ```

## Features
- **Reconstruction Filter**: Uses a high-quality neighbor-max search for realistic blurring of overlapping objects.
- **Frame Blending**: A low-cost alternative for a "trailing" or "ghosting" effect, useful for stylized visuals.
- **Camera & Object Motion**: Blurs both camera movement and individual moving GameObjects.

## How to Use
1. Add the `Motion` component to your Main Camera.
2. Ensure your rendering settings (or individual Mesh Renderers) have **Motion Vectors** enabled.
3. Adjust the **Shutter Speed** and **Sample Count** to balance quality and performance.

### Tips for Unity 6
If you are using the **Universal Render Pipeline (URP)** or **HDRP**, ensure your Render Pipeline Asset has "Motion Vectors" enabled in the quality settings to allow the effect to read object velocity data.

## License
MIT (See [LICENSE.txt](LICENSE.txt) for details)
