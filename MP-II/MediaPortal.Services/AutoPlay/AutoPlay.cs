#region Copyright (C) 2007-2008 Team MediaPortal

/*
    Copyright (C) 2007-2008 Team MediaPortal
    http://www.team-mediaportal.com
 
    This file is part of MediaPortal II

    MediaPortal II is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    MediaPortal II is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with MediaPortal II.  If not, see <http://www.gnu.org/licenses/>.
*/

#endregion

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

using MediaPortal.Core;
using MediaPortal.Core.AutoPlay;
using MediaPortal.Core.Collections;
using MediaPortal.Core.Localisation;
using MediaPortal.Core.Logging;
using MediaPortal.Core.MediaManager;
using MediaPortal.Core.Messaging;
using MediaPortal.Core.PathManager;
using MediaPortal.Core.Players;
using MediaPortal.Core.Settings;
using MediaPortal.Core.WindowManager;

using MediaPortal.Utilities.CD;
using MediaPortal.Utilities.Screens;

namespace MediaPortal.Services.AutoPlay
{
  public class AutoPlay : IAutoPlay
  {
    #region Variables
    private ILogger Logger;
    private DeviceVolumeMonitor _deviceMonitor;
    private AutoPlaySettings _settings;

    enum MediaType
    {
      UNKNOWN = 0,
      DVD = 1,
      AUDIO_CD = 2,
      PHOTOS = 3,
      VIDEOS = 4,
      AUDIO = 5
    }
    #endregion

    #region ctor / dtor
    public AutoPlay()
    {
      // We need BASS CD Support, so we need to load the plugin
      BassRegistration.BassRegistration.Register();
      string decoderFolderPath = ServiceScope.Get<IPathManager>().GetPath(@"<APPLICATION_ROOT>\musicplayer\plugins\audio decoders");
      int pluginHandle = Un4seen.Bass.Bass.BASS_PluginLoad(decoderFolderPath + "\\basscd.dll");

      Logger = ServiceScope.Get<ILogger>();
      LoadSettings();
    }

    ~AutoPlay()
    {
      if (_deviceMonitor != null)
        _deviceMonitor.Dispose();

      _deviceMonitor = null;
    }
    #endregion

    #region IAutoPlay implementation
    public void StartListening(IntPtr aHandle)
    {
      try
      {
        _deviceMonitor = new DeviceVolumeMonitor(aHandle);
        _deviceMonitor.OnVolumeInserted += new DeviceVolumeAction(VolumeInserted);
        _deviceMonitor.OnVolumeRemoved += new DeviceVolumeAction(VolumeRemoved);
        _deviceMonitor.AsynchronousEvents = true;
        _deviceMonitor.Enabled = true;

        Logger.Info("AutoPlay: Monitoring System for Media Changes");
      }
      catch (DeviceVolumeMonitorException ex)
      {
        Logger.Error("AutoPlay: Error enabling AutoPlay Service. {0}", ex.Message);
      }
    }

    public void StopListening()
    {
      if (_deviceMonitor != null)
        _deviceMonitor.Dispose();

      _deviceMonitor = null;
    }

    public void ExamineVolume(string strDrive)
    {
      if (strDrive == null) return;
      if (strDrive.Length == 0) return;

      switch (DetectMediaType(strDrive))
      {
        case MediaType.DVD:
          Logger.Info("AutoPlay: DVD inserted into {0}", strDrive);
          bool PlayDVD = false;
          if (_settings.AutoPlayDVD == "Yes")
          {
            Logger.Info("Autoplay: DVD AutoPlay = �es");
            PlayDVD = true;
          }
          else if ((_settings.AutoPlayDVD == "Ask") && (ShouldWeAutoPlay(MediaType.DVD)))
          {
            PlayDVD = true;
          }
          if (PlayDVD)
          {
            // Play DVD
            IWindow window = ServiceScope.Get<IWindowManager>().CurrentWindow;
            try
            {
              //window.WaitCursorVisible = true;
              PlayerCollection collection = ServiceScope.Get<PlayerCollection>();
              IPlayerFactory factory = ServiceScope.Get<IPlayerFactory>();
              IMediaItem mediaItem = new AutoPlayMediaItem(strDrive + @"\VIDEO_TS\VIDEO_TS.IFO");
              mediaItem.Title = "DVD";
              mediaItem.MetaData["MimeType"] = "audio";
              IPlayer player = factory.GetPlayer(mediaItem);


              //play it
              player.Play(mediaItem);
              collection.Add(player);
              if (player.CanResumeSession(null))
              {
                player.Paused = true;
                ServiceScope.Get<IWindowManager>().ShowDialog("movieResume");
              }

            }
            finally
            {
              //window.WaitCursorVisible = false;
            }
            IWindowManager manager = (IWindowManager)ServiceScope.Get<IWindowManager>();
            // We need to show the movies window first, otherwise we'll have problems returning back from full screen on stop.
            manager.ShowWindow("movies");
            manager.ShowWindow("fullscreenvideo");
          }
          break;

        case MediaType.AUDIO_CD:
          Logger.Info("AutoPlay: Audio CD inserted into drive {0}", strDrive);
          bool PlayAudioCd = false;
          if (_settings.AutoPlayCD == "Yes")
          {
            // Automaticaly play the CD
            PlayAudioCd = true;
            Logger.Info("Autplay: CD Autoplay = yes");
          }
          else if ((_settings.AutoPlayCD == "Ask") && (ShouldWeAutoPlay(MediaType.AUDIO_CD)))
          {
            PlayAudioCd = true;
          }
          if (PlayAudioCd)
          {
            // Play Audio CD
            IWindow window = ServiceScope.Get<IWindowManager>().CurrentWindow;
            try
            {
              //window.WaitCursorVisible = true;
              // Get the files of the Audio CD via the MediaManager
              // This does call the MusicImporter, which does a FreeDB Query
              IMediaManager mediaManager = ServiceScope.Get<IMediaManager>();
              List<IAbstractMediaItem> tracks = mediaManager.GetView(strDrive + @"\");

              // Add all items of the CD to the Playlist
              IPlaylistManager playList = ServiceScope.Get<IPlaylistManager>();
              foreach (IAbstractMediaItem item in tracks)
              {
                IMediaItem mediaItem = item as IMediaItem;
                if (mediaItem != null)
                {
                  mediaItem.MetaData["MimeType"] = "audio";
                  playList.PlayList.Add(mediaItem);
                }
              }
              playList.PlayAt(0);
            }
            finally
            {
              //window.WaitCursorVisible = false;
            }
          }
          break;

        case MediaType.PHOTOS:
          Logger.Info("AutoPlay: Photo volume inserted {0}", strDrive);
          if (ShouldWeAutoPlay(MediaType.PHOTOS))
          {
          }
          break;

        case MediaType.VIDEOS:
          Logger.Info("AutoPlay: Video volume inserted {0}", strDrive);
          if (ShouldWeAutoPlay(MediaType.VIDEOS))
          {
          }
          break;

        case MediaType.AUDIO:
          Logger.Info("AutoPlay: Audio volume inserted {0}", strDrive);
          if (ShouldWeAutoPlay(MediaType.AUDIO))
          {
          }
          break;

        default:
          Logger.Info("AutoPlay: Unknown media type inserted into drive {0}", strDrive);
          break;
      }
    }
    #endregion

    #region Events
    /// <summary>
    /// The event that gets triggered whenever a new volume is inserted.
    /// </summary>	
    private void VolumeInserted(int bitMask)
    {
      string driveLetter = _deviceMonitor.MaskToLogicalPaths(bitMask);
      Logger.Info("AutoPlay: Media inserted in drive {0}", driveLetter);

      MPMessage msg = new MPMessage();
      msg.MetaData["drive"] = driveLetter;
      msg.MetaData["action"] = "Inserted";
      IQueue queue = ServiceScope.Get<IMessageBroker>().Get("autoplay");
      queue.Send(msg);

      ExamineVolume(driveLetter);
    }

    /// <summary>
    /// The event that gets triggered whenever a volume is removed.
    /// </summary>	
    private void VolumeRemoved(int bitMask)
    {
      string driveLetter = _deviceMonitor.MaskToLogicalPaths(bitMask);
      Logger.Info("AutoPlay: Media removed from drive {0}", driveLetter);

      MPMessage msg = new MPMessage();
      msg.MetaData["drive"] = driveLetter;
      msg.MetaData["action"] = "Removed";
      IQueue queue = ServiceScope.Get<IMessageBroker>().Get("autoplay");
      queue.Send(msg);
    }
    #endregion

    #region Private Methods
    private void LoadSettings()
    {
      _settings = new AutoPlaySettings();
      ServiceScope.Get<ISettingsManager>().Load(_settings);
    }

    /// <summary>
    /// Detects the media type of the CD/DVD inserted into a drive.
    /// </summary>
    /// <param name="driveLetter">The drive that contains the data.</param>
    /// <returns>The media type of the drive.</returns>
    private MediaType DetectMediaType(string strDrive)
    {
      if (strDrive == null)
        return MediaType.UNKNOWN;

      if (strDrive == string.Empty)
        return MediaType.UNKNOWN;

      try
      {
        if (Directory.Exists(strDrive + "\\VIDEO_TS"))
          return MediaType.DVD;

        if (Utils.isARedBookCD(strDrive))
          return MediaType.AUDIO_CD;

        List<string> allfiles = new List<string>();
        GetAllFiles(strDrive + "\\", ref allfiles);

        // Have a look at MediaInfo dll how to detect mime types.
        /*
        foreach (string FileName in allfiles)
        {
          string ext = System.IO.Path.GetExtension(FileName).ToLower();
          if (MediaPortal.Util.Utils.IsVideo(FileName)) return MediaType.VIDEOS;
        }

        foreach (string FileName in allfiles)
        {
          string ext = System.IO.Path.GetExtension(FileName).ToLower();
          if (MediaPortal.Util.Utils.IsAudio(FileName)) return MediaType.AUDIO;
        }

        foreach (string FileName in allfiles)
        {
          if (MediaPortal.Util.Utils.IsPicture(FileName)) return MediaType.PHOTOS;
        }
        */
      }
      catch (Exception)
      {
      }
      return MediaType.UNKNOWN;
    }

    private bool ShouldWeAutoPlay(MediaType iMedia)
    {
      string line;

      ServiceScope.Get<IWindowManager>().DialogTitle = ServiceScope.Get<ILocalisation>().ToString("autoplay", "autoplay");
      
      switch (iMedia)
      {
        case MediaType.AUDIO:
        case MediaType.AUDIO_CD:
          line = ServiceScope.Get<ILocalisation>().ToString("autoplay", "audio");
          break;

        case MediaType.DVD:
          line = ServiceScope.Get<ILocalisation>().ToString("autoplay", "dvd");
          break;

        case MediaType.PHOTOS:
          line = ServiceScope.Get<ILocalisation>().ToString("autoplay", "photo");
          break;

        case MediaType.VIDEOS:
          line = ServiceScope.Get<ILocalisation>().ToString("autoplay", "video");
          break;

        default:
          line = ServiceScope.Get<ILocalisation>().ToString("autoplay", "disc");
          break;
      }
      ServiceScope.Get<IWindowManager>().DialogLine1 = line;
      ServiceScope.Get<IWindowManager>().ShowDialog("dialogYesNo");
      return ServiceScope.Get<IWindowManager>().GetDialogResponse();
    }


    private void GetAllFiles(string strFolder, ref List<string> allfiles)
    {
      if (strFolder == null) return;
      if (strFolder.Length == 0) return;
      if (allfiles == null) return;

      try
      {
        string[] files = System.IO.Directory.GetFiles(strFolder);
        if (files != null && files.Length > 0)
        {
          for (int i = 0; i < files.Length; ++i) allfiles.Add(files[i]);
        }
        string[] folders = System.IO.Directory.GetDirectories(strFolder);
        if (folders != null && folders.Length > 0)
        {
          for (int i = 0; i < folders.Length; ++i) GetAllFiles(folders[i], ref allfiles);
        }
      }
      catch (Exception)
      {
      }
    }

    #endregion
  }
}
