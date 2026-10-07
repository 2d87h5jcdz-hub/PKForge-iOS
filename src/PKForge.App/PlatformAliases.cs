// PKForge-iOS: the background music player is a platform class; shared code reaches it by this name.
#if ANDROID
global using PlatformMusicPlayer = PKForge.App.Platforms.Android.MusicPlayer;
#elif IOS
global using PlatformMusicPlayer = PKForge.App.Platforms.iOS.MusicPlayer;
#endif
