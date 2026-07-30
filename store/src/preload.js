'use strict';

const { contextBridge, ipcRenderer } = require('electron');

/**
 * The entire surface the renderer is allowed to touch.
 *
 * Note what is absent: there is no way to name a URL to download, a path to execute, or a
 * shortcut to write. The renderer refers to catalogue entries by id and the main process
 * decides what that means, so a careless or compromised renderer cannot be talked into
 * fetching and running something arbitrary.
 */
contextBridge.exposeInMainWorld('nullprice', {
  loadCatalogue: () => ipcRenderer.invoke('catalogue:load'),
  listInstalled: () => ipcRenderer.invoke('installed:list'),
  checkUpdates: () => ipcRenderer.invoke('updates:check'),

  install: (id) => ipcRenderer.invoke('install:start', id),
  cancelInstall: (id) => ipcRenderer.invoke('install:cancel', id),
  uninstall: (id) => ipcRenderer.invoke('install:uninstall', id),

  launch: (id) => ipcRenderer.invoke('app:launch', id),
  reveal: (id) => ipcRenderer.invoke('app:reveal', id),
  openExternal: (url) => ipcRenderer.invoke('shell:external', url),

  onProgress: (handler) => subscribe('install:progress', handler),
  onPhase: (handler) => subscribe('install:phase', handler),
  onDone: (handler) => subscribe('install:done', handler),
  onStoreUpdateReady: (handler) => subscribe('store:update-ready', handler),
});

function subscribe(channel, handler) {
  const listener = (_event, payload) => handler(payload);
  ipcRenderer.on(channel, listener);
  return () => ipcRenderer.removeListener(channel, listener);
}
