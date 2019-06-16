﻿using Remotely.Desktop.Win.Controls;
using Remotely.Desktop.Win.Services;
using Remotely.Shared.Models;
using Remotely.ScreenCast.Core;
using Remotely.ScreenCast.Core.Capture;
using Remotely.ScreenCast.Core.Models;
using Remotely.ScreenCast.Core.Utilities;
using Remotely.ScreenCast.Win;
using Remotely.ScreenCast.Win.Capture;
using Remotely.ScreenCast.Win.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Security.Principal;
using System.Security.Claims;

namespace Remotely.Desktop.Win.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        public MainWindowViewModel()
        {
            Current = this;
            Conductor = new Conductor();
            Conductor.SessionIDChanged += SessionIDChanged;
            Conductor.ViewerRemoved += ViewerRemoved;
            Conductor.ViewerAdded += ViewerAdded;
            Conductor.ScreenCastRequested += ScreenCastRequested;
            CursorIconWatcher = new CursorIconWatcher(Conductor);
            CursorIconWatcher.OnChange += CursorIconWatcher_OnChange;
            AudioCapturer = new AudioCapturer(Conductor);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public static MainWindowViewModel Current { get; private set; }

        public AudioCapturer AudioCapturer { get; private set; }

        public bool AllowHostChange
        {
            get
            {
                return string.IsNullOrWhiteSpace(ForceHost);
            }
        }

        public Conductor Conductor { get; }

        public Config Config { get; private set; }

        public CursorIconWatcher CursorIconWatcher { get; private set; }

        public string ForceHost { get; }

        public string Host
        {
            get
            {
                return Config?.Host;
            }
        }

        public string SessionID { get; set; }

        public ObservableCollection<Viewer> Viewers { get; } = new ObservableCollection<Viewer>();

        public void FirePropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task Init()
        {
            SessionID = "Retrieving...";
            Config = Config.GetConfig();
            if (AllowHostChange)
            {
                while (string.IsNullOrWhiteSpace(Config.Host))
                {
                    Config.Host = "https://";
                    PromptForHostName();
                }
            }
            else
            {
                Config.Host = ForceHost;
            }
            
            Conductor.ProcessArgs(new string[] { "-mode", "Normal", "-host", Config.Host });
            try
            {
                await Conductor.Connect();
            }
            catch (Exception ex)
            {
                Logger.Write(ex);
                MessageBox.Show("Failed to connect to server.", "Connection Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Conductor.SetMessageHandlers(new WinInput());
            await Conductor.CasterSocket.SendDeviceInfo(Conductor.ServiceID, Environment.MachineName);
            await Conductor.CasterSocket.GetSessionID();

            CheckForAdminRights();
        }

        private void CheckForAdminRights()
        {
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            {
                var result = MessageBox.Show("Remotely isn't running with administrator rights.  Would you like to re-launch as an admin?", "Run as Admin", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location);
                    psi.Verb = "RunAs";
                    Process.Start(psi);
                    Environment.Exit(0);
                }
            }
        }

        public void PromptForHostName()
        {
            var prompt = new HostNamePrompt();
            if (!string.IsNullOrWhiteSpace(Config.Host))
            {
                HostNamePromptViewModel.Current.Host = Config.Host;
            }
            prompt.Owner = App.Current?.MainWindow;
            prompt.ShowDialog();
            var result = HostNamePromptViewModel.Current.Host.TrimEnd("/".ToCharArray());
            if (!result.StartsWith("https://") && !result.StartsWith("http://"))
            {
                result = $"https://{result}";
            }
            if (result != Config.Host)
            {
                Config.Host = result;
                Config.Save();
                FirePropertyChanged("Host");
            }
        }

        internal void CopyLink()
        {
            Clipboard.SetText($"{Host}/RemoteControl?sessionID={SessionID.Replace(" ", "")}");
        }

        internal async Task RemoveViewers(IEnumerable<Viewer> viewerList)
        {
            foreach (Viewer viewer in viewerList)
            {
                viewer.DisconnectRequested = true;
                await Conductor.CasterSocket.SendViewerRemoved(viewer.ViewerConnectionID);
            }
        }

        private async void CursorIconWatcher_OnChange(object sender, CursorInfo cursor)
        {
            if (Conductor?.CasterSocket != null)
            {
                await Conductor?.CasterSocket?.SendCursorChange(cursor, Conductor.Viewers.Keys.ToList());
            }
        }
        private void ScreenCastRequested(object sender, ScreenCastRequest screenCastRequest)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                var result = MessageBox.Show($"You've received a connection request from {screenCastRequest.RequesterName}.  Accept?", "Connection Request", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result == MessageBoxResult.Yes)
                {
                    Task.Run(async() =>
                    {
                        ICapturer capturer;
                        try
                        {
                            if (Conductor.Viewers.Count == 0)
                            {
                                capturer = new DXCapture();
                            }
                            else
                            {
                                capturer = new BitBltCapture();
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Write(ex);
                            capturer = new BitBltCapture();
                        }
                        await Conductor.CasterSocket.SendCursorChange(CursorIconWatcher.GetCurrentCursor(), new List<string>() { screenCastRequest.ViewerID });
                        ScreenCaster.BeginScreenCasting(screenCastRequest.ViewerID, screenCastRequest.RequesterName, capturer, Conductor);
                        AudioCapturer.Start();
                    });
                }
            });
        }
        private void SessionIDChanged(object sender, string sessionID)
        {
            var formattedSessionID = "";
            for (var i = 0; i < sessionID.Length; i += 3)
            {
                formattedSessionID += sessionID.Substring(i, 3) + " ";
            }
            SessionID = formattedSessionID.Trim();
        }

        private void ViewerAdded(object sender, Viewer viewer)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                Viewers.Add(viewer);
            });
        }

        private void ViewerRemoved(object sender, string viewerID)
        {
            App.Current.Dispatcher.Invoke(() =>
            {
                var viewer = Viewers.FirstOrDefault(x => x.ViewerConnectionID == viewerID);
                if (viewer != null)
                {
                    Viewers.Remove(viewer);
                }
            });
        }
    }
}