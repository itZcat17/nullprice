'use strict';

const { contextBridge, ipcRenderer } = require('electron');

/**
 * The entire surface the renderer is allowed to touch.
 *
 * Note what is absent: there is no way to name a URL to download or a path to execute.
 * The renderer can only refer to catalogue entries by id, and the main process decides
 * what that means. A compromised or careless renderer therefore cannot be talked into
 * fetching and running something arbitrary.
 */
contextBridge.exposeInMainWorld('nullprice', {
  loadCatalogue: () => ipcRenderer.invoke('catalogue:load'),

  startInstall: (id) => ipcRenderer.invoke('install:start', id),
  cancelInstall: (id) => ipcRenderer.invoke('install:cancel', id),
  runInstaller: (id) => ipcRenderer.invoke('install:run', id),
  revealDownloads: () => ipcRenderer.invoke('install:reveal'),

  openExternal: (url) => ipcRenderer.invoke('shell:external', url),

  onProgress: (handler) => {
    const listener = (_event, payload) => handler(payload);
    ipcRenderer.on('install:progress', listener);
    return () => ipcRenderer.removeListener('install:progress', listener);
  },

  onReady: (handler) => {
    const listener = (_event, payload) => handler(payload);
    ipcRenderer.on('install:ready', listener);
    return () => ipcRenderer.removeListener('install:ready', listener);
  },
});
