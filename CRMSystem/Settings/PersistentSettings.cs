﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using CRMSystem.Cryptography.Windows;
using CRMSystem.Settings.Entities;
using RIS.Settings.Ini;
using Environment = RIS.Environment;

namespace CRMSystem.Settings
{
    public sealed class PersistentSettings
    {
        public const string SettingsFileName = "PersistentSettings.store";



        public event EventHandler<AvailableUsersChangedEventArgs> AvailableUsersChanged;
        public event EventHandler<UserChangedEventArgs> CurrentUserChanged;



        public object SyncRoot { get; }
        public string SettingsFilePath { get; }
        public IniFile SettingsFile { get; }

        public object AvailableUsersSyncRoot { get; }
        public ReadOnlyDictionary<string, User> AvailableUsers { get; private set; }
        public User CurrentUser { get; private set; }



        public PersistentSettings()
        {
            SyncRoot = new object();

            SettingsFilePath = Path.Combine(
                Environment.ExecProcessDirectoryName,
                SettingsFileName);
            SettingsFile = new IniFile();

            AvailableUsersSyncRoot = new object();

            CurrentUser = new User(
                null,
                null,
                UserStoreType.Unknown);

            Load();

            UpdateAvailableUsers();

            var currentUserLogin =
                GetCurrentUserLogin();

            if (string.IsNullOrEmpty(currentUserLogin))
            {
                ResetCurrentUserLogin();

                return;
            }

            if (!SetCurrentUser(currentUserLogin))
            {
                RemoveUser(currentUserLogin);
            }
        }



        public void Load()
        {
            lock (SyncRoot)
            {
                SettingsFile.Load(SettingsFilePath);
            }
        }

        public void Save()
        {
            Task.Factory.StartNew(() =>
            {
                lock (SyncRoot)
                {
                    SettingsFile.Save();
                }
            });
        }



        public IniSection GetSection(string sectionName)
        {
            try
            {
                lock (SyncRoot)
                {
                    return SettingsFile.GetSection(sectionName);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void RemoveSection(string sectionName)
        {
            lock (SyncRoot)
            {
                SettingsFile.RemoveSection(sectionName);

                Save();
            }
        }



        public string GetDefault(
            string settingName, string defaultValue = null)
        {
            return Get(SettingsFile.DefaultSectionName, settingName, defaultValue);
        }

        public string Get(string sectionName,
            string settingName, string defaultValue = null)
        {
            lock (SyncRoot)
            {
                return SettingsFile.GetString(sectionName, settingName, defaultValue);
            }
        }


        public void SetDefault(
            string settingName, string value)
        {
            Set(SettingsFile.DefaultSectionName, settingName, value);
        }

        public void Set(string sectionName,
            string settingName, string value)
        {
            lock (SyncRoot)
            {
                SettingsFile.Set(sectionName, settingName, value);

                Save();
            }
        }


        public void RemoveDefault(
            string settingName)
        {
            Remove(SettingsFile.DefaultSectionName, settingName);
        }

        public void Remove(string sectionName,
            string settingName)
        {
            lock (SyncRoot)
            {
                SettingsFile.Remove(sectionName, settingName);

                Save();
            }
        }



        public void UpdateAvailableUsers()
        {
            lock (AvailableUsersSyncRoot)
            {
                var defaultSectionName = SettingsFile.DefaultSectionName;
                var userLogins = SettingsFile.GetSections()
                    .Where(userLogin =>
                        userLogin != defaultSectionName)
                    .OrderBy(
                        login => login,
                        StringComparer.Ordinal)
                    .ToArray();
                var users = new Dictionary<string, User>(userLogins.Length);

                foreach (var login in userLogins)
                {
                    try
                    {
                        users.Add(
                            login,
                            new User(
                                login,
                                GetUserPassword(login),
                                UserStoreType.Permanent));
                    }
                    catch (CryptographicException)
                    {
                        RemoveUser(login, false);
                    }
                    catch (FormatException)
                    {
                        RemoveUser(login, false);
                    }
                }

                var oldAvailableUsers = AvailableUsers;
                AvailableUsers = new ReadOnlyDictionary<string, User>(users);

                AvailableUsersChanged?.Invoke(this,
                    new AvailableUsersChangedEventArgs(oldAvailableUsers, AvailableUsers));
            }
        }

        public bool IsExistUser(string login)
        {
            if (string.IsNullOrEmpty(login))
                return false;

            if (CurrentUser.Login == login
                && CurrentUser.IsTemporary())
            {
                return true;
            }

            return AvailableUsers.ContainsKey(login);
        }


        public bool SetCurrentUser(string login)
        {
            if (!IsExistUser(login))
                return false;

            if (!GetUser(login, out var currentUser))
                return false;

            SetCurrentUserLogin(login);

            var oldUser = CurrentUser;
            CurrentUser = currentUser;

            CurrentUserChanged?.Invoke(this,
                new UserChangedEventArgs(oldUser, CurrentUser));

            return true;
        }

        public bool SetCurrentUserTemporary(string login,
            string password)
        {
            ResetCurrentUserLogin();

            var oldUser = CurrentUser;
            CurrentUser = new User(
                login,
                password,
                UserStoreType.Temporary);

            CurrentUserChanged?.Invoke(this,
                new UserChangedEventArgs(oldUser, CurrentUser));

            return true;
        }


        public void ResetCurrentUser()
        {
            ResetCurrentUserLogin();

            var oldUser = CurrentUser;
            CurrentUser = new User(
                null,
                null,
                UserStoreType.Unknown);

            CurrentUserChanged?.Invoke(this,
                new UserChangedEventArgs(oldUser, CurrentUser));
        }



        public string GetCurrentUserLogin()
        {
            if (CurrentUser.IsTemporary())
                return CurrentUser.Login;

            var currentUserLogin =
                GetDefault("CurrentUserLogin");

            try
            {
                return WindowsCipherManager.Decrypt(
                    currentUserLogin,
                    "CurrentUserLogin");
            }
            catch (CryptographicException)
            {
                SetCurrentUserLogin(
                    currentUserLogin);

                return WindowsCipherManager.Decrypt(
                    GetDefault("CurrentUserLogin"),
                    "CurrentUserLogin");
            }
            catch (FormatException)
            {
                SetCurrentUserLogin(
                    currentUserLogin);

                return WindowsCipherManager.Decrypt(
                    GetDefault("CurrentUserLogin"),
                    "CurrentUserLogin");
            }
        }


        public void SetCurrentUserLogin(string login)
        {
            if (CurrentUser.Login == login
                && CurrentUser.IsTemporary())
            {
                return;
            }

            SetDefault("CurrentUserLogin",
                WindowsCipherManager.Encrypt(
                    login,
                    "CurrentUserLogin"));
        }


        public void ResetCurrentUserLogin()
        {
            if (CurrentUser.IsTemporary())
                return;

            SetCurrentUserLogin(string.Empty);
        }



        public bool GetUser(string login, out User user)
        {
            if (CurrentUser.Login == login
                && CurrentUser.IsTemporary())
            {
                user = CurrentUser;

                return true;
            }

            user = new User(
                null,
                null,
                UserStoreType.Unknown);

            var userSection = GetSection(login);

            if (userSection == null)
                return false;

            try
            {
                user = new User(
                    login,
                    GetUserPassword(login),
                    UserStoreType.Permanent);
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }

            return true;
        }

        public string GetUserPassword(string login)
        {
            if (CurrentUser.Login == login
                && CurrentUser.IsTemporary())
            {
                return CurrentUser.Password;
            }

            return WindowsCipherManager.Decrypt(
                Get(login, "UserPassword"),
                $"UserPassword-{login}");
        }


        public bool SetUser(string login, string password,
            bool updateAvailableUsers = true)
        {
            if (CurrentUser.Login == login
                && CurrentUser.IsTemporary())
            {
                CurrentUser = new User(
                    CurrentUser.Login,
                    password,
                    CurrentUser.StoreType);

                return true;
            }

            try
            {
                SetUserPassword(login, password ?? string.Empty);
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }

            if (updateAvailableUsers)
                UpdateAvailableUsers();

            return true;
        }

        public void SetUserPassword(string login,
            string password)
        {
            if (CurrentUser.Login == login
                && CurrentUser.IsTemporary())
            {
                CurrentUser = new User(
                    CurrentUser.Login,
                    password,
                    CurrentUser.StoreType);

                return;
            }

            Set(login, "UserPassword",
                WindowsCipherManager.Encrypt(
                    password,
                    $"UserPassword-{login}"));
        }



        public void RemoveUser(string login,
            bool updateAvailableUsers = true)
        {
            if (CurrentUser.Login == login
                && CurrentUser.IsTemporary())
            {
                ResetCurrentUser();

                return;
            }

            RemoveSection(login);

            if (GetCurrentUserLogin() == login)
                ResetCurrentUser();

            if (updateAvailableUsers)
                UpdateAvailableUsers();
        }
    }
}
