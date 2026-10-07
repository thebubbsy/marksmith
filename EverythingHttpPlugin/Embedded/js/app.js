/**
 * OmniSight - Everything Web Suite Master Client Application
 */

document.addEventListener('DOMContentLoaded', () => {
  // Grid Sizing & Density Constants
  const GRID_BOUNDS = {
    size: { min: 140, max: 420, default: 220 },
    gap: { min: 4, max: 48, default: 20 },
    cardPadding: { min: 4, max: 24, default: 14 },
  };

  function clampGridValue(val, bounds) {
    const n = Number(val);
    return Number.isFinite(n) ? Math.max(bounds.min, Math.min(bounds.max, Math.round(n))) : bounds.default;
  }

  // Application State
  const state = {
    query: '',
    category: 'all',
    offset: 0,
    count: 48,
    sort: 'date_modified',
    ascending: 0,
    modifiers: {
      case: false,
      regex: false,
      wholeword: false,
      matchpath: false,
    },
    viewMode: localStorage.getItem('everything_view_mode') || 'grid',
    theme: localStorage.getItem('everything_theme') || 'dark',
    gridConfig: {
      size: clampGridValue(localStorage.getItem('everything_grid_size'), GRID_BOUNDS.size),
      gap: clampGridValue(localStorage.getItem('everything_grid_gap'), GRID_BOUNDS.gap),
      cardPadding: clampGridValue(localStorage.getItem('everything_grid_card_padding'), GRID_BOUNDS.cardPadding),
    },
    currentFolder: '',
    drives: [],
    googleDrive: {
      installed: false,
      running: false,
      mounted: false,
      mountPath: null,
      status: 'unknown',
    },
    results: [],
    totalResults: 0,
    selectedIndex: -1,
    previewIndex: -1,
    previewState: {
      zoom: 1,
      rotation: 0,
    },
    currentUser: null,
  };

  // DOM Elements
  const searchInput = document.getElementById('searchInput');
  const clearSearchBtn = document.getElementById('clearSearchBtn');
  const categoryTabs = document.getElementById('categoryTabs');
  const viewGridBtn = document.getElementById('viewGridBtn');
  const viewTableBtn = document.getElementById('viewTableBtn');
  const gridSettingsDropdown = document.getElementById('gridSettingsDropdown');
  const gridSettingsBtn = document.getElementById('gridSettingsBtn');
  const gridSettingsPopover = document.getElementById('gridSettingsPopover');
  const resetGridSettingsBtn = document.getElementById('resetGridSettingsBtn');
  const gridCardSizeSlider = document.getElementById('gridCardSizeSlider');
  const gridSizeValBadge = document.getElementById('gridSizeValBadge');
  const gridGapSlider = document.getElementById('gridGapSlider');
  const gridGapValBadge = document.getElementById('gridGapValBadge');
  const gridPaddingSlider = document.getElementById('gridPaddingSlider');
  const gridPaddingValBadge = document.getElementById('gridPaddingValBadge');
  const gridSizePresets = document.getElementById('gridSizePresets');
  const gridGapPresets = document.getElementById('gridGapPresets');
  const gridPaddingPresets = document.getElementById('gridPaddingPresets');
  const sortSelect = document.getElementById('sortSelect');
  const sortDirectionBtn = document.getElementById('sortDirectionBtn');
  const themeToggleBtn = document.getElementById('themeToggleBtn');
  const helpModalBtn = document.getElementById('helpModalBtn');
  const logoutBtn = document.getElementById('logoutBtn');
  const brandBtn = document.getElementById('brandBtn');
  const gdriveStatusBadge = document.getElementById('gdriveStatusBadge');
  const gdriveStatusText = document.getElementById('gdriveStatusText');

  const modCaseBtn = document.getElementById('modCaseBtn');
  const modRegexBtn = document.getElementById('modRegexBtn');
  const modWholeBtn = document.getElementById('modWholeBtn');
  const modPathBtn = document.getElementById('modPathBtn');
  const chipLargeBtn = document.getElementById('chipLargeBtn');
  const chipGigBtn = document.getElementById('chipGigBtn');
  const chipTodayBtn = document.getElementById('chipTodayBtn');
  const chipGdriveBtn = document.getElementById('chipGdriveBtn');

  // Breadcrumb & Directory Explorer Elements
  const breadcrumbBar = document.getElementById('breadcrumbBar');
  const breadcrumbTrail = document.getElementById('breadcrumbTrail');
  const breadcrumbHomeBtn = document.getElementById('breadcrumbHomeBtn');
  const navUpFolderBtn = document.getElementById('navUpFolderBtn');
  const drivesList = document.getElementById('drivesList');

  const resultsCount = document.getElementById('resultsCount');
  const latencyBadge = document.getElementById('latencyBadge');
  const loadingState = document.getElementById('loadingState');
  const emptyState = document.getElementById('emptyState');
  const mediaGrid = document.getElementById('mediaGrid');
  const tableContainer = document.getElementById('tableContainer');
  const filesTableBody = document.getElementById('filesTableBody');

  const prevPageBtn = document.getElementById('prevPageBtn');
  const nextPageBtn = document.getElementById('nextPageBtn');
  const pageIndicator = document.getElementById('pageIndicator');

  const inspectorSidebar = document.getElementById('inspectorSidebar');
  const closeInspectorBtn = document.getElementById('closeInspectorBtn');
  const inspectorPreviewBox = document.getElementById('inspectorPreviewBox');
  const inspectorName = document.getElementById('inspectorName');
  const inspectorPath = document.getElementById('inspectorPath');
  const inspectorSize = document.getElementById('inspectorSize');
  const inspectorModified = document.getElementById('inspectorModified');
  const inspectorCategory = document.getElementById('inspectorCategory');
  const inspectorBrowseBtn = document.getElementById('inspectorBrowseBtn');
  const inspectorPreviewBtn = document.getElementById('inspectorPreviewBtn');
  const inspectorRevealBtn = document.getElementById('inspectorRevealBtn');
  const inspectorDownloadBtn = document.getElementById('inspectorDownloadBtn');
  const inspectorCopyPathBtn = document.getElementById('inspectorCopyPathBtn');

  const previewModal = document.getElementById('previewModal');
  const modalTypeBadge = document.getElementById('modalTypeBadge');
  const modalTitle = document.getElementById('modalTitle');
  const modalBody = document.getElementById('modalBody');
  const closeModalBtn = document.getElementById('closeModalBtn');
  const modalDownloadBtn = document.getElementById('modalDownloadBtn');
  const zoomInBtn = document.getElementById('zoomInBtn');
  const zoomOutBtn = document.getElementById('zoomOutBtn');
  const resetZoomBtn = document.getElementById('resetZoomBtn');
  const rotateBtn = document.getElementById('rotateBtn');
  const copyCodeBtn = document.getElementById('copyCodeBtn');

  const shortcutsModal = document.getElementById('shortcutsModal');
  const closeShortcutsBtn = document.getElementById('closeShortcutsBtn');
  const userAvatarText = document.getElementById('userAvatarText');
  const userNameText = document.getElementById('userNameText');
  const engineBadgeText = document.getElementById('engineBadgeText');

  // Initialize Theme
  applyTheme(state.theme);

  // Initialize View Mode
  applyViewMode(state.viewMode);

  // Initialize Grid Layout & Spacing Configuration
  applyGridConfig(state.gridConfig, false);

  // Auth & Session Check
  checkAuth();

  // Search Debouncer
  let searchTimeout = null;
  function triggerSearchDebounced(delay = 250) {
    clearTimeout(searchTimeout);
    searchTimeout = setTimeout(() => {
      state.offset = 0;
      executeSearch();
    }, delay);
  }

  // Check Authentication Status
  async function checkAuth() {
    try {
      const res = await fetch('/api/auth/status');
      const data = await res.json();
      if (!data.authenticated) {
        window.location.href = '/login';
        return;
      }
      state.currentUser = data.user;
      userNameText.textContent = data.user || 'User';
      userAvatarText.textContent = (data.user || 'U').charAt(0).toUpperCase();

      if (data.everythingConnected) {
        engineBadgeText.textContent = `Everything Online (${data.everythingPort})`;
      } else {
        engineBadgeText.textContent = `Everything Offline`;
        engineBadgeText.parentElement.style.color = '#fb7185';
      }

      // Initial Drives & Search
      loadDrives();
      executeSearch();
    } catch (err) {
      window.location.href = '/login';
    }
  }

  // Directory & Drives Explorer Handlers
  async function loadDrives() {
    try {
      const res = await fetch('/api/drives');
      const data = await res.json();

      if (data.googleDrive) {
        state.googleDrive = data.googleDrive;
        updateGoogleDriveBadge(data.googleDrive);
      }

      if (data.drives && data.drives.length) {
        state.drives = data.drives;
        drivesList.innerHTML = '';
        data.drives.forEach(drive => {
          const detail = (data.driveDetails || []).find(d => d.path === drive);
          const isGDrive = detail ? detail.isGoogleDrive : (drive.toUpperCase().startsWith('G:'));

          const chip = document.createElement('button');
          chip.className = `drive-chip ${isGDrive ? 'drive-chip-gdrive' : ''}`;
          chip.innerHTML = isGDrive ? `<span class="gdrive-icon">▲</span> ${escapeHtml(drive)} (Drive)` : escapeHtml(drive);
          chip.title = isGDrive ? `Browse Google Drive (${drive})` : `Browse Drive ${drive}`;
          chip.onclick = () => navigateToFolder(drive);
          drivesList.appendChild(chip);
        });

        // If Google Drive is mounted to a non-drive-letter folder (e.g. mirrored folder in user profile)
        if (data.googleDrive && data.googleDrive.mounted && data.googleDrive.mountPath && !data.drives.some(d => data.googleDrive.mountPath.toUpperCase().startsWith(d.toUpperCase()))) {
          const gdriveFolderChip = document.createElement('button');
          gdriveFolderChip.className = 'drive-chip drive-chip-gdrive';
          gdriveFolderChip.innerHTML = '<span class="gdrive-icon">▲</span> Google Drive';
          gdriveFolderChip.title = `Browse Google Drive (${data.googleDrive.mountPath})`;
          gdriveFolderChip.onclick = () => navigateToFolder(data.googleDrive.mountPath);
          drivesList.appendChild(gdriveFolderChip);
        }

        // If Google Drive is running / installed on machine but not yet mounted as a drive letter
        if (data.googleDrive && data.googleDrive.running && !data.googleDrive.mounted) {
          const gdriveChip = document.createElement('button');
          gdriveChip.className = 'drive-chip drive-chip-gdrive drive-chip-sign-in';
          gdriveChip.innerHTML = '<span class="gdrive-icon">▲</span> Google Drive (Sign In)';
          gdriveChip.title = 'Google Drive for Desktop is running. Click to prompt sign-in on your desktop.';
          gdriveChip.onclick = () => triggerGoogleDriveLaunch();
          drivesList.appendChild(gdriveChip);
        } else if (data.googleDrive && data.googleDrive.installed && !data.googleDrive.running && !data.googleDrive.mounted) {
          const gdriveChip = document.createElement('button');
          gdriveChip.className = 'drive-chip drive-chip-gdrive drive-chip-offline';
          gdriveChip.innerHTML = '<span class="gdrive-icon">▲</span> Google Drive (Stopped)';
          gdriveChip.title = 'Google Drive for Desktop is installed. Click to launch.';
          gdriveChip.onclick = () => triggerGoogleDriveLaunch();
          drivesList.appendChild(gdriveChip);
        }
      }
    } catch (err) {
      console.warn('Could not load drives', err);
    }
  }

  async function triggerGoogleDriveLaunch() {
    try {
      showToast('Triggering Google Drive sign-in prompt on desktop...');
      const res = await fetch('/api/gdrive/launch', { method: 'POST' });
      const data = await res.json();
      if (data.success) {
        showToast('Google Drive authentication prompted on desktop! Please complete sign-in to mount G:\\');
      } else {
        showToast('Google Drive app is active. Please complete sign-in via system tray to mount G:\\');
      }
    } catch {
      showToast('Please sign in via the Google Drive icon in your Windows system tray.');
    }
  }

  function updateGoogleDriveBadge(gdrive) {
    if (!gdriveStatusBadge || !gdriveStatusText) return;
    if (gdrive.mounted) {
      gdriveStatusText.textContent = `▲ Drive (${gdrive.mountPath || 'G:\\'}) Online`;
      gdriveStatusBadge.className = 'brand-badge brand-badge-gdrive status-mounted';
      gdriveStatusBadge.title = `Google Drive is mounted at ${gdrive.mountPath || 'G:\\'}. Native range streaming ready.`;
    } else if (gdrive.running) {
      gdriveStatusText.textContent = '▲ Drive (Sign-in Required)';
      gdriveStatusBadge.className = 'brand-badge brand-badge-gdrive status-awaiting';
      gdriveStatusBadge.title = 'Google Drive desktop app is active. Click to trigger sign-in prompt on desktop.';
    } else if (gdrive.installed) {
      gdriveStatusText.textContent = '▲ Drive Stopped';
      gdriveStatusBadge.className = 'brand-badge brand-badge-gdrive status-stopped';
      gdriveStatusBadge.title = 'Google Drive for Desktop is installed but not currently running. Click to launch.';
    } else {
      gdriveStatusText.textContent = '▲ Drive Offline';
      gdriveStatusBadge.className = 'brand-badge brand-badge-gdrive status-none';
      gdriveStatusBadge.title = 'Google Drive for Desktop is not detected.';
    }
  }

  function navigateToFolder(folderPath) {
    state.currentFolder = folderPath || '';
    state.offset = 0;
    updateBreadcrumbs();
    executeSearch();
  }

  function updateBreadcrumbs() {
    breadcrumbTrail.innerHTML = '';
    if (!state.currentFolder) {
      breadcrumbHomeBtn.classList.add('active');
      navUpFolderBtn.disabled = true;
      return;
    }

    breadcrumbHomeBtn.classList.remove('active');
    navUpFolderBtn.disabled = false;
    const norm = state.currentFolder.replace(/\\/g, '/');
    const parts = norm.split('/').filter(Boolean);
    let accum = '';

    parts.forEach((part, idx) => {
      accum += (idx === 0 ? part : '/' + part);
      let targetPath = accum;
      if (idx === 0 && part.includes(':')) {
        targetPath = part + '\\';
      } else {
        targetPath = accum.replace(/\//g, '\\');
      }

      const sep = document.createElement('span');
      sep.className = 'breadcrumb-sep';
      sep.textContent = '›';
      breadcrumbTrail.appendChild(sep);

      const btn = document.createElement('button');
      btn.className = `breadcrumb-btn ${idx === parts.length - 1 ? 'active' : ''}`;
      const isGDriveCrumb = (idx === 0 && (part.toUpperCase() === 'G:' || (state.googleDrive && state.googleDrive.mountPath && targetPath.toLowerCase().startsWith(state.googleDrive.mountPath.toLowerCase())))) ||
                            part.toLowerCase() === 'google drive' || part.toLowerCase() === 'my drive';
      if (isGDriveCrumb) {
        btn.innerHTML = `<span class="gdrive-icon" style="color: #34a853; font-weight: bold; margin-right: 3px;">▲</span> ${escapeHtml(part)}`;
        btn.title = `Google Drive: ${targetPath}`;
      } else {
        btn.textContent = part;
        btn.title = targetPath;
      }
      btn.onclick = () => navigateToFolder(targetPath);
      breadcrumbTrail.appendChild(btn);
    });
  }

  function goUpOneFolder() {
    if (!state.currentFolder) return;
    const norm = state.currentFolder.replace(/\\+$/, '').replace(/\\/g, '/');
    const lastSlash = norm.lastIndexOf('/');
    if (lastSlash >= 0) {
      let parent = norm.slice(0, lastSlash);
      if (parent.endsWith(':')) parent += '\\';
      else parent = parent.replace(/\//g, '\\');
      navigateToFolder(parent);
    } else {
      navigateToFolder('');
    }
  }

  breadcrumbHomeBtn.addEventListener('click', () => navigateToFolder(''));
  navUpFolderBtn.addEventListener('click', goUpOneFolder);

  async function revealInExplorer(filePath) {
    if (!filePath) return;
    try {
      const res = await fetch('/api/open', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action: 'reveal', path: filePath }),
      });
      const data = await res.json();
      if (data.success) {
        showToast('Revealed in Windows File Explorer');
      } else {
        showToast(data.error || 'Could not reveal file');
      }
    } catch (err) {
      showToast('Error communicating with desktop');
    }
  }

  async function openFileLocally(filePath) {
    if (!filePath) return;
    try {
      const res = await fetch('/api/open', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ action: 'open', path: filePath }),
      });
      const data = await res.json();
      if (data.success) {
        showToast('Opened with default application');
      } else {
        showToast(data.error || 'Could not open file');
      }
    } catch (err) {
      showToast('Error communicating with desktop');
    }
  }

  // Main Search Execution
  async function executeSearch() {
    loadingState.style.display = 'block';
    emptyState.style.display = 'none';

    const params = new URLSearchParams({
      q: state.query,
      category: state.category,
      offset: state.offset.toString(),
      count: state.count.toString(),
      sort: state.sort,
      ascending: state.ascending.toString(),
    });

    if (state.currentFolder) params.set('folder', state.currentFolder);
    if (state.modifiers.case) params.set('case', '1');
    if (state.modifiers.regex) params.set('regex', '1');
    if (state.modifiers.wholeword) params.set('wholeword', '1');
    if (state.modifiers.matchpath) params.set('matchpath', '1');

    try {
      const res = await fetch(`/api/search?${params.toString()}`);
      if (res.status === 401) {
        window.location.href = '/login';
        return;
      }

      const data = await res.json();
      loadingState.style.display = 'none';

      if (!res.ok) {
        showToast(data.error || 'Search error');
        return;
      }

      state.results = data.results || [];
      state.totalResults = data.totalResults || 0;

      // Update Header Stats
      resultsCount.textContent = `${state.totalResults.toLocaleString()} items`;
      latencyBadge.textContent = `⚡ ${data.durationMs}ms`;

      // Update Pagination UI
      updatePagination();

      // Render Results
      if (state.results.length === 0) {
        emptyState.style.display = 'flex';
        mediaGrid.innerHTML = '';
        filesTableBody.innerHTML = '';
        clearInspector();
      } else {
        renderResults();
        // Select first item by default if none selected
        selectItem(0);
      }
    } catch (err) {
      loadingState.style.display = 'none';
      showToast('Error connecting to search backend');
    }
  }

  // Render Results depending on View Mode
  function renderResults() {
    if (state.viewMode === 'grid') {
      renderGridView();
    } else {
      renderTableView();
    }
  }

  // Render Grid Cards
  function renderGridView() {
    mediaGrid.innerHTML = '';
    state.results.forEach((item, index) => {
      const card = document.createElement('div');
      card.className = `media-card ${state.selectedIndex === index ? 'selected' : ''}`;
      card.dataset.index = index;

      // Thumbnail content based on item type
      let thumbHtml = '';
      if (item.canPreviewImage) {
        thumbHtml = `
          <img 
            class="card-thumbnail-img" 
            loading="lazy" 
            src="${item.thumbnailUrl || (item.existsLocally ? item.streamUrl : '')}" 
            alt="${escapeHtml(item.name)}"
            onerror="this.style.display='none'; this.nextElementSibling.style.display='flex';"
          />
          <div class="card-icon-fallback" ${item.thumbnailUrl ? 'style="display: none;"' : ''}>🖼️</div>
        `;
      } else if (item.canPreviewVideo) {
        const canHoverPlay = !item.isCloudOnly && item.existsLocally !== false;
        thumbHtml = `
          <img 
            class="card-thumbnail-img video-thumb-img" 
            loading="lazy" 
            src="${item.thumbnailUrl || ''}" 
            alt="${escapeHtml(item.name)}"
            onerror="this.style.display='none'; this.nextElementSibling.style.display='flex';"
          />
          <div class="card-icon-fallback" ${item.thumbnailUrl ? 'style="display: none;"' : ''}>🎬</div>
          ${canHoverPlay ? `
            <video class="card-hover-video" preload="none" muted loop playsinline src="${item.streamUrl}"></video>
            <div class="video-play-hint" title="Hover to preview">▶</div>
          ` : ''}
        `;
      } else if (item.canPreviewAudio) {
        thumbHtml = `
          ${item.thumbnailUrl ? `
            <img 
              class="card-thumbnail-img" 
              loading="lazy" 
              src="${item.thumbnailUrl}" 
              alt="${escapeHtml(item.name)}"
              onerror="this.style.display='none'; this.nextElementSibling.style.display='flex';"
            />
          ` : ''}
          <div class="card-icon-fallback" ${item.thumbnailUrl ? 'style="display: none;"' : ''}>🎵</div>
        `;
      } else if (item.isFolder || item.type === 'folder') {
        thumbHtml = `
          <div class="card-icon-fallback" style="color: #6366f1;">📁</div>
        `;
      } else {
        thumbHtml = `
          <div class="card-icon-fallback">${getCategoryEmoji(item.category)}</div>
        `;
      }

      const isDir = item.isFolder || item.type === 'folder';

      card.innerHTML = `
        <div class="card-thumbnail-wrapper">
          ${thumbHtml}
          <div class="card-ext-badge cat-${item.category}">
            ${escapeHtml(item.extension ? item.extension.toUpperCase() : (isDir ? 'DIR' : 'FILE'))}
          </div>
          ${item.isCloudOnly ? '<div class="card-cloud-badge" title="Stored in OneDrive (Online Only)">☁️ Cloud</div>' : ''}
          ${item.isGoogleDrive ? '<div class="card-gdrive-badge" title="Google Drive File (Native Stream Enabled)"><span class="gdrive-icon">▲</span> Drive</div>' : ''}
          ${item.sizeFormatted !== '-' ? `<div class="card-size-badge">${escapeHtml(item.sizeFormatted)}</div>` : ''}
          <div class="card-overlay-actions">
            <button class="overlay-action-btn preview-act" title="${isDir ? 'Browse Folder' : 'QuickLook (Space)'}">${isDir ? '📂' : '👁️'}</button>
            ${!isDir ? '<button class="overlay-action-btn download-act" title="Download">⬇️</button>' : ''}
            <button class="overlay-action-btn copy-act" title="Copy Path">📋</button>
          </div>
        </div>
        <div class="card-info">
          <div class="card-filename" title="${escapeHtml(item.name)}">${escapeHtml(item.name)}</div>
          <div class="card-meta-row">
            <span class="card-path-snippet" title="${escapeHtml(item.path)}">${escapeHtml(item.path || '')}</span>
            <span>${escapeHtml(item.dateModifiedRelative)}</span>
          </div>
        </div>
      `;

      // Hover Video Preview for Video Cards (Safeguarded against offline/cloud files)
      if (item.canPreviewVideo && !item.isCloudOnly && item.existsLocally !== false) {
        const hoverVideo = card.querySelector('.card-hover-video');
        if (hoverVideo) {
          hoverVideo.addEventListener('error', () => {
            hoverVideo.style.display = 'none';
          });
          card.addEventListener('mouseenter', () => {
            hoverVideo.classList.add('playing');
            hoverVideo.play().catch(() => {});
          });
          card.addEventListener('mouseleave', () => {
            hoverVideo.classList.remove('playing');
            hoverVideo.pause();
            hoverVideo.currentTime = 1;
          });
        }
      }

      // Card Events
      card.addEventListener('click', (e) => {
        if (e.target.closest('.preview-act')) {
          e.stopPropagation();
          if (isDir) {
            navigateToFolder(item.fullPath);
          } else {
            openPreview(index);
          }
          return;
        }
        if (e.target.closest('.download-act')) {
          e.stopPropagation();
          triggerDownload(item);
          return;
        }
        if (e.target.closest('.copy-act')) {
          e.stopPropagation();
          copyToClipboard(item.fullPath, 'File path copied to clipboard');
          return;
        }
        selectItem(index);
      });

      card.addEventListener('dblclick', () => {
        if (isDir) {
          navigateToFolder(item.fullPath);
        } else {
          openPreview(index);
        }
      });

      mediaGrid.appendChild(card);
    });
  }

  // Render Table Rows
  function renderTableView() {
    filesTableBody.innerHTML = '';
    state.results.forEach((item, index) => {
      const isDir = item.isFolder || item.type === 'folder';
      const tr = document.createElement('tr');
      tr.className = state.selectedIndex === index ? 'selected' : '';
      tr.dataset.index = index;

      tr.innerHTML = `
        <td>
          <div class="table-file-name">
            <span class="table-file-icon">${isDir ? '📁' : (item.isGoogleDrive ? '▲' : getCategoryEmoji(item.category))}</span>
            <span title="${escapeHtml(item.name)}">${escapeHtml(item.name)}</span>
            ${item.isGoogleDrive ? '<span class="table-gdrive-badge" title="Google Drive Native Stream">▲ Drive</span>' : ''}
            ${item.isCloudOnly ? '<span class="table-cloud-badge" title="OneDrive Cloud File">☁️ Cloud</span>' : ''}
          </div>
        </td>
        <td style="font-family: var(--font-mono); font-size: 0.8rem;">${escapeHtml(item.sizeFormatted)}</td>
        <td style="color: var(--text-muted);">${escapeHtml(item.dateModifiedRelative)}</td>
        <td style="color: var(--text-dim); font-size: 0.8rem;" title="${escapeHtml(item.path)}">${escapeHtml(item.path)}</td>
      `;

      tr.addEventListener('click', () => selectItem(index));
      tr.addEventListener('dblclick', () => {
        if (isDir) {
          navigateToFolder(item.fullPath);
        } else {
          openPreview(index);
        }
      });

      filesTableBody.appendChild(tr);
    });
  }

  // Select Item and Populate Inspector
  function selectItem(index) {
    if (index < 0 || index >= state.results.length) return;
    state.selectedIndex = index;
    const item = state.results[index];
    const isDir = item.isFolder || item.type === 'folder';

    // Highlight in UI
    document.querySelectorAll('.media-card').forEach((card, idx) => {
      card.classList.toggle('selected', idx === index);
    });
    document.querySelectorAll('.files-table tbody tr').forEach((row, idx) => {
      row.classList.toggle('selected', idx === index);
    });

    // Populate Inspector Sidebar
    inspectorName.textContent = item.name;
    inspectorPath.textContent = item.fullPath;
    inspectorSize.textContent = `${item.sizeFormatted} (${item.sizeBytes.toLocaleString()} bytes)`;
    inspectorModified.textContent = item.dateModified ? new Date(item.dateModified).toLocaleString() : '-';
    inspectorCategory.textContent = item.isGoogleDrive ? `${item.category.toUpperCase()} • GOOGLE DRIVE` : item.category.toUpperCase();

    // Inspector Action Buttons Configuration
    if (isDir) {
      inspectorBrowseBtn.style.display = 'inline-flex';
      inspectorBrowseBtn.onclick = () => navigateToFolder(item.fullPath);
      inspectorPreviewBtn.style.display = 'none';
      inspectorDownloadBtn.style.display = 'none';
      inspectorPreviewBox.innerHTML = `<span style="font-size: 3.5rem; color: #6366f1;">📁</span>`;
    } else {
      inspectorBrowseBtn.style.display = 'none';
      inspectorPreviewBtn.style.display = 'inline-flex';
      inspectorDownloadBtn.style.display = 'inline-flex';

      // Live Rich Inspector Previews
      if (item.canPreviewImage) {
        inspectorPreviewBox.innerHTML = `<img src="${item.thumbnailUrl || (item.existsLocally ? item.streamUrl : '')}" alt="Preview" style="cursor: pointer;" onclick="openPreview(${index})" />`;
      } else if (item.canPreviewVideo) {
        if (item.isCloudOnly) {
          inspectorPreviewBox.innerHTML = `
            <div style="text-align: center; padding: 1rem; color: var(--text-muted); font-size: 0.85rem;">
              <div style="font-size: 2rem; margin-bottom: 0.25rem;">☁️</div>
              <div>OneDrive Cloud File</div>
            </div>
          `;
        } else if (item.existsLocally === false) {
          inspectorPreviewBox.innerHTML = `
            <div style="text-align: center; padding: 1rem; color: var(--text-muted); font-size: 0.85rem;">
              <div style="font-size: 2rem; margin-bottom: 0.25rem;">⚠️</div>
              <div>File Not Local</div>
            </div>
          `;
        } else {
          inspectorPreviewBox.innerHTML = `
            <div class="inspector-mini-player">
              <video controls preload="metadata" playsinline src="${item.streamUrl}"></video>
            </div>
          `;
          const vid = inspectorPreviewBox.querySelector('video');
          if (vid) {
            vid.addEventListener('error', () => {
              inspectorPreviewBox.innerHTML = `
                <div style="text-align: center; padding: 1rem; color: var(--text-muted); font-size: 0.85rem;">
                  <div style="font-size: 2rem; margin-bottom: 0.25rem;">⚠️</div>
                  <div>Preview Unavailable</div>
                </div>
              `;
            });
          }
        }
      } else if (item.canPreviewAudio) {
        if (item.isCloudOnly || item.existsLocally === false) {
          inspectorPreviewBox.innerHTML = `
            <div style="text-align: center; padding: 1rem; color: var(--text-muted); font-size: 0.85rem;">
              <div style="font-size: 2rem; margin-bottom: 0.25rem;">${item.isCloudOnly ? '☁️' : '⚠️'}</div>
              <div>${item.isCloudOnly ? 'OneDrive Cloud Audio' : 'Audio Not Local'}</div>
            </div>
          `;
        } else {
          inspectorPreviewBox.innerHTML = `
            <div class="inspector-mini-audio">
              <span style="font-size: 2rem;">🎵</span>
              <audio controls preload="metadata" src="${item.streamUrl}"></audio>
            </div>
          `;
        }
      } else if (item.canPreviewText) {
        if (item.isCloudOnly || item.existsLocally === false) {
          inspectorPreviewBox.innerHTML = `
            <div style="text-align: center; padding: 1rem; color: var(--text-muted); font-size: 0.85rem;">
              <div style="font-size: 2rem; margin-bottom: 0.25rem;">${item.isCloudOnly ? '☁️' : '⚠️'}</div>
              <div>${item.isCloudOnly ? 'OneDrive Cloud Document' : 'Document Not Local'}</div>
            </div>
          `;
        } else {
          inspectorPreviewBox.innerHTML = `<div class="inspector-code-snippet">Loading snippet...</div>`;
          fetch(`/api/text-content?path=${encodeURIComponent(item.fullPath)}`)
            .then((r) => r.json())
            .then((data) => {
              const snippetBox = inspectorPreviewBox.querySelector('.inspector-code-snippet');
              if (snippetBox) {
                snippetBox.textContent = data.content ? data.content.slice(0, 1500) : (data.error || 'Empty file');
              }
            })
            .catch(() => {
              const snippetBox = inspectorPreviewBox.querySelector('.inspector-code-snippet');
              if (snippetBox) snippetBox.textContent = 'Preview unavailable';
            });
        }
      } else {
        inspectorPreviewBox.innerHTML = `<span style="font-size: 3.5rem;">${getCategoryEmoji(item.category)}</span>`;
      }
    }

    // Windows Native Explorer Reveal Action
    inspectorRevealBtn.onclick = () => revealInExplorer(item.fullPath);
  }

  function clearInspector() {
    inspectorName.textContent = '-';
    inspectorPath.textContent = '-';
    inspectorSize.textContent = '-';
    inspectorModified.textContent = '-';
    inspectorCategory.textContent = '-';
    inspectorBrowseBtn.style.display = 'none';
    inspectorPreviewBtn.style.display = 'inline-flex';
    inspectorDownloadBtn.style.display = 'inline-flex';
    inspectorPreviewBox.innerHTML = `<span style="font-size: 3rem; color: var(--text-dim);">📄</span>`;
  }

  // Update Pagination Controls
  function updatePagination() {
    const totalPages = Math.ceil(state.totalResults / state.count) || 1;
    const currentPage = Math.floor(state.offset / state.count) + 1;

    pageIndicator.textContent = `Page ${currentPage} of ${totalPages} (${state.totalResults.toLocaleString()} items)`;
    prevPageBtn.disabled = state.offset === 0;
    nextPageBtn.disabled = state.offset + state.count >= state.totalResults;
  }

  // Open Media Preview Modal (Lightbox / Players)
  async function openPreview(index) {
    if (index < 0 || index >= state.results.length) return;
    state.previewIndex = index;
    const item = state.results[index];

    if (item.isGoogleDrive) {
      modalTitle.innerHTML = `<span class="modal-gdrive-tag">▲ Google Drive</span> ${escapeHtml(item.name)}`;
    } else {
      modalTitle.textContent = item.name;
    }
    modalTypeBadge.textContent = item.extension ? item.extension.toUpperCase() : 'FILE';
    modalTypeBadge.className = `card-ext-badge cat-${item.category}`;

    // Reset Zoom / Rotation
    state.previewState.zoom = 1;
    state.previewState.rotation = 0;

    // Reset Toolbars
    document.querySelectorAll('.img-ctrl').forEach(el => el.style.display = item.canPreviewImage ? 'inline-flex' : 'none');
    copyCodeBtn.style.display = item.canPreviewText ? 'inline-flex' : 'none';

    // Clear Previous Modal Content and cleanly unload active media
    const prevVideo = modalBody.querySelector('video');
    if (prevVideo) {
      prevVideo.pause();
      prevVideo.removeAttribute('src');
      prevVideo.load();
    }
    const prevAudio = modalBody.querySelector('audio');
    if (prevAudio) {
      prevAudio.pause();
      prevAudio.removeAttribute('src');
      prevAudio.load();
    }
    modalBody.innerHTML = '';

    if (item.isCloudOnly) {
      // Cloud-Only OneDrive File Notice
      const container = document.createElement('div');
      container.style.cssText = 'text-align: center; padding: 2.5rem 1.5rem;';
      container.innerHTML = `
        <div style="font-size: 3.5rem; margin-bottom: 0.75rem;">☁️</div>
        <h3 style="font-size: 1.25rem; font-weight: 600; margin-bottom: 0.5rem; color: var(--text-main);">OneDrive Online-Only File</h3>
        <p style="color: var(--text-muted); max-width: 440px; margin: 0 auto 1.5rem; line-height: 1.5; font-size: 0.9rem;">
          This file is stored in Microsoft OneDrive cloud storage and is not currently downloaded on this PC. Open or locate the file to trigger OneDrive sync.
        </p>
        <div style="display: flex; gap: 0.75rem; justify-content: center; flex-wrap: wrap;">
          <button class="primary-btn" id="modalLocateBtn" style="padding: 0.6rem 1.2rem;">📂 Locate in Explorer</button>
          <button class="secondary-btn" id="modalOpenAppBtn" style="padding: 0.6rem 1.2rem;">⚡ Open with Default App</button>
        </div>
      `;
      container.querySelector('#modalLocateBtn').onclick = () => revealInExplorer(item.fullPath);
      container.querySelector('#modalOpenAppBtn').onclick = () => openFileLocally(item.fullPath);
      modalBody.appendChild(container);
    } else if (item.existsLocally === false) {
      // File Not Found Locally Notice
      const container = document.createElement('div');
      container.style.cssText = 'text-align: center; padding: 2.5rem 1.5rem;';
      container.innerHTML = `
        <div style="font-size: 3.5rem; margin-bottom: 0.75rem;">⚠️</div>
        <h3 style="font-size: 1.25rem; font-weight: 600; margin-bottom: 0.5rem; color: var(--text-main);">File Not Found Locally</h3>
        <p style="color: var(--text-muted); max-width: 440px; margin: 0 auto 1.5rem; line-height: 1.5; font-size: 0.9rem;">
          This file is indexed in Everything but does not currently exist at the expected path on the local disk.
        </p>
        <div style="display: flex; gap: 0.75rem; justify-content: center; flex-wrap: wrap;">
          <button class="primary-btn" id="modalLocateBtn" style="padding: 0.6rem 1.2rem;">📂 Locate in Explorer</button>
        </div>
      `;
      container.querySelector('#modalLocateBtn').onclick = () => revealInExplorer(item.fullPath);
      modalBody.appendChild(container);
    } else if (item.canPreviewImage) {
      // Image Lightbox
      const container = document.createElement('div');
      container.className = 'image-viewer-container';

      const img = document.createElement('img');
      img.className = 'image-viewer-img';
      img.src = item.streamUrl;
      img.alt = item.name;

      // Nav Arrows
      const prevBtn = document.createElement('button');
      prevBtn.className = 'nav-arrow-btn nav-arrow-left';
      prevBtn.innerHTML = '‹';
      prevBtn.title = 'Previous Image (Left Arrow)';
      prevBtn.onclick = (e) => { e.stopPropagation(); navigatePreview(-1); };

      const nextBtn = document.createElement('button');
      nextBtn.className = 'nav-arrow-btn nav-arrow-right';
      nextBtn.innerHTML = '›';
      nextBtn.title = 'Next Image (Right Arrow)';
      nextBtn.onclick = (e) => { e.stopPropagation(); navigatePreview(1); };

      container.appendChild(prevBtn);
      container.appendChild(img);
      container.appendChild(nextBtn);
      modalBody.appendChild(container);
    } else if (item.canPreviewVideo) {
      // HTML5 Video Player with HTTP Range Seeking and Anchored Floating Speed Controls
      const container = document.createElement('div');
      container.className = 'video-player-container';
      container.innerHTML = `
        <video controls autoplay preload="metadata" playsinline>
          <source src="${item.streamUrl}">
          Your browser does not support HTML5 video streaming.
        </video>
        <div class="video-speed-controls" role="toolbar" aria-label="Playback Speed Controls">
          <span class="video-speed-label">Speed:</span>
          <button class="view-toggle-btn speed-btn" data-speed="0.5" aria-pressed="false" title="0.5x Speed">0.5x</button>
          <button class="view-toggle-btn speed-btn active" data-speed="1.0" aria-pressed="true" title="1.0x Normal Speed">1.0x</button>
          <button class="view-toggle-btn speed-btn" data-speed="1.25" aria-pressed="false" title="1.25x Speed">1.25x</button>
          <button class="view-toggle-btn speed-btn" data-speed="1.5" aria-pressed="false" title="1.5x Speed">1.5x</button>
          <button class="view-toggle-btn speed-btn" data-speed="2.0" aria-pressed="false" title="2.0x Speed">2.0x</button>
        </div>
      `;
      const vid = container.querySelector('video');
      vid.addEventListener('error', () => {
        container.innerHTML = `
          <div style="text-align: center; padding: 2.5rem 1rem;">
            <div style="font-size: 3rem; margin-bottom: 0.75rem;">⚠️</div>
            <h4 style="color: var(--text-main); margin-bottom: 0.5rem;">Playback Unavailable in Browser</h4>
            <p style="color: var(--text-muted); font-size: 0.875rem; max-width: 400px; margin: 0 auto 1.25rem;">
              This video container or codec (e.g. HEVC/MKV) cannot be decoded directly by the browser. Open with desktop media player.
            </p>
            <div style="display: flex; gap: 0.75rem; justify-content: center;">
              <button class="primary-btn" id="modalVideoOpenBtn">⚡ Open with Player</button>
              <button class="secondary-btn" id="modalVideoRevealBtn">📂 Locate in Explorer</button>
            </div>
          </div>
        `;
        const openBtn = container.querySelector('#modalVideoOpenBtn');
        if (openBtn) openBtn.onclick = () => openFileLocally(item.fullPath);
        const revBtn = container.querySelector('#modalVideoRevealBtn');
        if (revBtn) revBtn.onclick = () => revealInExplorer(item.fullPath);
      });

      const syncSpeedUi = (rate) => {
        container.querySelectorAll('.speed-btn').forEach(b => {
          const isMatch = Math.abs(parseFloat(b.dataset.speed) - rate) < 0.05;
          b.classList.toggle('active', isMatch);
          b.setAttribute('aria-pressed', isMatch ? 'true' : 'false');
        });
      };

      container.querySelectorAll('.speed-btn').forEach(sbtn => {
        sbtn.onclick = (e) => {
          e.stopPropagation();
          const targetSpeed = parseFloat(sbtn.dataset.speed);
          if (vid) vid.playbackRate = parseFloat(sbtn.dataset.speed);
          syncSpeedUi(targetSpeed);
        };
      });

      if (vid) {
        vid.addEventListener('ratechange', () => {
          syncSpeedUi(vid.playbackRate);
        });
      }
      modalBody.appendChild(container);
    } else if (item.canPreviewAudio) {
      // Audio Player with Waveform Box
      const container = document.createElement('div');
      container.className = 'audio-player-container';
      container.innerHTML = `
        <div class="audio-art-box">🎵</div>
        <h3 style="font-size: 1.1rem; text-align: center; color: var(--text-main);">${escapeHtml(item.name)}</h3>
        <audio controls autoplay preload="metadata">
          <source src="${item.streamUrl}">
        </audio>
      `;
      modalBody.appendChild(container);
    } else if (item.canPreviewPdf) {
      // Embedded PDF Viewer
      const iframe = document.createElement('iframe');
      iframe.className = 'pdf-viewer-frame';
      iframe.src = item.streamUrl;
      modalBody.appendChild(iframe);
    } else if (item.canPreviewText) {
      // Code & Text Viewer
      const container = document.createElement('div');
      container.className = 'code-viewer-container';
      container.innerHTML = `<div style="padding: 2rem; color: var(--text-dim);">Loading file contents...</div>`;
      modalBody.appendChild(container);

      try {
        const res = await fetch(`/api/text-content?path=${encodeURIComponent(item.fullPath)}`);
        const textData = await res.json();
        if (textData.content !== undefined) {
          container.innerHTML = `
            <div class="code-viewer-content" id="codeContentBox">${escapeHtml(textData.content)}</div>
          `;
          if (textData.truncated) {
            showToast('Large file: preview truncated to first 512KB');
          }
        } else {
          container.innerHTML = `<div style="padding: 2rem; color: #fb7185;">Could not read file contents: ${textData.error}</div>`;
        }
      } catch (err) {
        container.innerHTML = `<div style="padding: 2rem; color: #fb7185;">Error fetching text preview</div>`;
      }
    } else if (item.isFolder || item.type === 'folder') {
      // Folder Peek Modal
      modalBody.innerHTML = `
        <div class="empty-state">
          <div class="empty-icon">📁</div>
          <div class="empty-title">${escapeHtml(item.name)}</div>
          <div class="empty-desc">Folder • ${escapeHtml(item.path)}</div>
          <div style="display: flex; gap: 0.75rem; justify-content: center; margin-top: 1.5rem; flex-wrap: wrap;">
            <button class="btn-primary" id="modalBrowseFolderBtn">
              <span>📂</span> Browse Inside Folder
            </button>
            <button class="btn-secondary" id="modalRevealFolderBtn">
              <span>🖥️</span> Reveal in Explorer
            </button>
          </div>
        </div>
      `;
      const browseBtn = modalBody.querySelector('#modalBrowseFolderBtn');
      if (browseBtn) browseBtn.onclick = () => { closePreview(); navigateToFolder(item.fullPath); };
      const revBtn = modalBody.querySelector('#modalRevealFolderBtn');
      if (revBtn) revBtn.onclick = () => revealInExplorer(item.fullPath);
    } else {
      // Generic File Info
      modalBody.innerHTML = `
        <div class="empty-state">
          <div class="empty-icon">${item.type === 'folder' ? '📁' : '📄'}</div>
          <div class="empty-title">${escapeHtml(item.name)}</div>
          <div class="empty-desc">${escapeHtml(item.sizeFormatted)} • ${escapeHtml(item.category.toUpperCase())}</div>
          <div style="display: flex; gap: 0.75rem; justify-content: center; margin-top: 1rem; flex-wrap: wrap;">
            <button class="btn-primary" onclick="window.location.href='${item.downloadUrl}'">
              <span>⬇️</span> Download File
            </button>
            <button class="btn-secondary" onclick="revealInExplorer('${escapeHtml(item.fullPath.replace(/\\/g, '\\\\'))}')">
              <span>🖥️</span> Reveal in Explorer
            </button>
          </div>
        </div>
      `;
    }

    previewModal.classList.add('active');
  }

  function navigatePreview(direction) {
    let nextIdx = state.previewIndex + direction;
    while (nextIdx >= 0 && nextIdx < state.results.length) {
      const candidate = state.results[nextIdx];
      if (candidate.canPreviewImage || candidate.canPreviewVideo || candidate.canPreviewAudio || candidate.canPreviewText || candidate.canPreviewPdf) {
        openPreview(nextIdx);
        selectItem(nextIdx);
        return;
      }
      nextIdx += direction;
    }
  }

  function closePreview() {
    // Stop any playing video or audio
    const video = modalBody.querySelector('video');
    if (video) video.pause();
    const audio = modalBody.querySelector('audio');
    if (audio) audio.pause();

    previewModal.classList.remove('active');
    modalBody.innerHTML = '';
    state.previewIndex = -1;
  }

  // Image Transform Helpers
  function updateImageTransform() {
    const img = modalBody.querySelector('.image-viewer-img');
    if (img) {
      img.style.transform = `scale(${state.previewState.zoom}) rotate(${state.previewState.rotation}deg)`;
    }
  }

  zoomInBtn.addEventListener('click', () => {
    state.previewState.zoom = Math.min(state.previewState.zoom + 0.25, 4);
    updateImageTransform();
  });

  zoomOutBtn.addEventListener('click', () => {
    state.previewState.zoom = Math.max(state.previewState.zoom - 0.25, 0.25);
    updateImageTransform();
  });

  resetZoomBtn.addEventListener('click', () => {
    state.previewState.zoom = 1;
    state.previewState.rotation = 0;
    updateImageTransform();
  });

  rotateBtn.addEventListener('click', () => {
    state.previewState.rotation = (state.previewState.rotation + 90) % 360;
    updateImageTransform();
  });

  copyCodeBtn.addEventListener('click', () => {
    const codeBox = document.getElementById('codeContentBox');
    if (codeBox) {
      copyToClipboard(codeBox.textContent, 'Code copied to clipboard');
    }
  });

  modalDownloadBtn.addEventListener('click', () => {
    if (state.previewIndex >= 0) {
      triggerDownload(state.results[state.previewIndex]);
    }
  });

  closeModalBtn.addEventListener('click', closePreview);
  previewModal.addEventListener('click', (e) => {
    if (e.target === previewModal) closePreview();
  });

  // Search Input Handlers
  searchInput.addEventListener('input', (e) => {
    state.query = e.target.value;
    clearSearchBtn.style.display = state.query ? 'block' : 'none';
    triggerSearchDebounced();
  });

  clearSearchBtn.addEventListener('click', () => {
    searchInput.value = '';
    state.query = '';
    clearSearchBtn.style.display = 'none';
    searchInput.focus();
    triggerSearchDebounced(50);
  });

  brandBtn.addEventListener('click', () => {
    searchInput.value = '';
    state.query = '';
    state.category = 'all';
    setActiveCategory('all');
    clearSearchBtn.style.display = 'none';
    triggerSearchDebounced(50);
  });

  // Category Tabs
  categoryTabs.addEventListener('click', (e) => {
    const btn = e.target.closest('.tab-btn');
    if (!btn) return;
    const cat = btn.dataset.category;
    if (cat === state.category) return;
    state.category = cat;
    setActiveCategory(cat);
    triggerSearchDebounced(50);
  });

  function setActiveCategory(cat) {
    document.querySelectorAll('.tab-btn').forEach(btn => {
      btn.classList.toggle('active', btn.dataset.category === cat);
    });
  }

  // Modifiers
  function setupModifier(btn, key) {
    btn.addEventListener('click', () => {
      state.modifiers[key] = !state.modifiers[key];
      btn.classList.toggle('active', state.modifiers[key]);
      triggerSearchDebounced(50);
    });
  }
  setupModifier(modCaseBtn, 'case');
  setupModifier(modRegexBtn, 'regex');
  setupModifier(modWholeBtn, 'wholeword');
  setupModifier(modPathBtn, 'matchpath');

  // Quick Chips
  chipLargeBtn.addEventListener('click', () => appendSearchToken('size:>100MB'));
  chipGigBtn.addEventListener('click', () => appendSearchToken('size:>1GB'));
  chipTodayBtn.addEventListener('click', () => appendSearchToken('dm:today'));
  if (chipGdriveBtn) {
    chipGdriveBtn.addEventListener('click', () => {
      const gdriveTab = categoryTabs.querySelector('[data-category="gdrive"]');
      if (gdriveTab) {
        gdriveTab.click();
      } else {
        appendSearchToken('g:\\');
      }
    });
  }

  if (gdriveStatusBadge) {
    gdriveStatusBadge.addEventListener('click', () => {
      if (state.googleDrive && state.googleDrive.mounted) {
        navigateToFolder(state.googleDrive.mountPath || 'G:\\');
        showToast(`Browsing Google Drive mount (${state.googleDrive.mountPath || 'G:\\'})`);
      } else if (state.googleDrive && state.googleDrive.running) {
        triggerGoogleDriveLaunch();
      } else if (state.googleDrive && state.googleDrive.installed) {
        triggerGoogleDriveLaunch();
      } else {
        showToast('Google Drive is not detected on this PC.');
      }
    });
  }

  function appendSearchToken(token) {
    if (!searchInput.value.includes(token)) {
      searchInput.value = (searchInput.value.trim() + ' ' + token).trim();
      state.query = searchInput.value;
      clearSearchBtn.style.display = 'block';
      triggerSearchDebounced(50);
    }
  }

  // View Mode Toggles
  viewGridBtn.addEventListener('click', () => applyViewMode('grid'));
  viewTableBtn.addEventListener('click', () => applyViewMode('table'));

  function applyViewMode(mode) {
    state.viewMode = mode;
    localStorage.setItem('everything_view_mode', mode);
    viewGridBtn.classList.toggle('active', mode === 'grid');
    viewTableBtn.classList.toggle('active', mode === 'table');
    mediaGrid.style.display = mode === 'grid' ? 'grid' : 'none';
    tableContainer.style.display = mode === 'table' ? 'block' : 'none';
    if (mode !== 'grid') {
      closeGridSettings();
    }
    renderResults();
  }

  // Grid Density & Spacing Customizer
  function applyGridConfig(config, persist = true) {
    if (config.size !== undefined) {
      const parsedSize = clampGridValue(config.size, GRID_BOUNDS.size);
      state.gridConfig.size = parsedSize;
      document.documentElement.style.setProperty('--grid-min-size', `${parsedSize}px`);
      if (persist) localStorage.setItem('everything_grid_size', parsedSize);
    }
    if (config.gap !== undefined) {
      const parsedGap = clampGridValue(config.gap, GRID_BOUNDS.gap);
      state.gridConfig.gap = parsedGap;
      document.documentElement.style.setProperty('--grid-gap', `${parsedGap}px`);
      if (persist) localStorage.setItem('everything_grid_gap', parsedGap);
    }
    if (config.cardPadding !== undefined) {
      const parsedPadding = clampGridValue(config.cardPadding, GRID_BOUNDS.cardPadding);
      state.gridConfig.cardPadding = parsedPadding;
      document.documentElement.style.setProperty('--grid-card-padding', `${parsedPadding}px`);
      if (persist) localStorage.setItem('everything_grid_card_padding', parsedPadding);
    }

    updateGridControlsUI();
  }

  function updateGridControlsUI() {
    if (gridCardSizeSlider) {
      gridCardSizeSlider.value = state.gridConfig.size;
      gridCardSizeSlider.setAttribute('aria-valuenow', state.gridConfig.size);
    }
    if (gridSizeValBadge) gridSizeValBadge.textContent = `${state.gridConfig.size}px`;

    if (gridGapSlider) {
      gridGapSlider.value = state.gridConfig.gap;
      gridGapSlider.setAttribute('aria-valuenow', state.gridConfig.gap);
    }
    if (gridGapValBadge) gridGapValBadge.textContent = `${state.gridConfig.gap}px`;

    if (gridPaddingSlider) {
      gridPaddingSlider.value = state.gridConfig.cardPadding;
      gridPaddingSlider.setAttribute('aria-valuenow', state.gridConfig.cardPadding);
    }
    if (gridPaddingValBadge) gridPaddingValBadge.textContent = `${state.gridConfig.cardPadding}px`;

    // Highlight active preset chips & set aria-pressed
    if (gridSizePresets) {
      gridSizePresets.querySelectorAll('.preset-chip').forEach(chip => {
        const isActive = parseInt(chip.dataset.size, 10) === state.gridConfig.size;
        chip.classList.toggle('active', isActive);
        chip.setAttribute('aria-pressed', isActive ? 'true' : 'false');
      });
    }
    if (gridGapPresets) {
      gridGapPresets.querySelectorAll('.preset-chip').forEach(chip => {
        const isActive = parseInt(chip.dataset.gap, 10) === state.gridConfig.gap;
        chip.classList.toggle('active', isActive);
        chip.setAttribute('aria-pressed', isActive ? 'true' : 'false');
      });
    }
    if (gridPaddingPresets) {
      gridPaddingPresets.querySelectorAll('.preset-chip').forEach(chip => {
        const isActive = parseInt(chip.dataset.cardPadding, 10) === state.gridConfig.cardPadding;
        chip.classList.toggle('active', isActive);
        chip.setAttribute('aria-pressed', isActive ? 'true' : 'false');
      });
    }
  }

  function openGridSettings() {
    if (!gridSettingsPopover) return;
    if (state.viewMode !== 'grid') applyViewMode('grid');
    gridSettingsPopover.removeAttribute('hidden');
    if (gridSettingsBtn) {
      gridSettingsBtn.setAttribute('aria-expanded', 'true');
      gridSettingsBtn.classList.add('active');
    }
  }

  function closeGridSettings() {
    if (!gridSettingsPopover) return;
    gridSettingsPopover.setAttribute('hidden', '');
    if (gridSettingsBtn) {
      gridSettingsBtn.setAttribute('aria-expanded', 'false');
      gridSettingsBtn.classList.remove('active');
    }
  }

  function toggleGridSettings() {
    if (!gridSettingsPopover) return;
    if (gridSettingsPopover.hasAttribute('hidden')) {
      openGridSettings();
    } else {
      closeGridSettings();
    }
  }

  if (gridSettingsBtn) {
    gridSettingsBtn.addEventListener('click', (e) => {
      e.stopPropagation();
      toggleGridSettings();
    });
  }

  document.addEventListener('click', (e) => {
    if (gridSettingsPopover && !gridSettingsPopover.hasAttribute('hidden')) {
      if (gridSettingsDropdown && !gridSettingsDropdown.contains(e.target)) {
        closeGridSettings();
      }
    }
  });

  if (gridCardSizeSlider) {
    gridCardSizeSlider.addEventListener('input', (e) => {
      applyGridConfig({ size: e.target.value });
      if (state.viewMode !== 'grid') applyViewMode('grid');
    });
  }

  if (gridGapSlider) {
    gridGapSlider.addEventListener('input', (e) => {
      applyGridConfig({ gap: e.target.value });
      if (state.viewMode !== 'grid') applyViewMode('grid');
    });
  }

  if (gridPaddingSlider) {
    gridPaddingSlider.addEventListener('input', (e) => {
      applyGridConfig({ cardPadding: e.target.value });
      if (state.viewMode !== 'grid') applyViewMode('grid');
    });
  }

  if (gridSizePresets) {
    gridSizePresets.addEventListener('click', (e) => {
      const chip = e.target.closest('.preset-chip');
      if (chip && chip.dataset.size) {
        applyGridConfig({ size: parseInt(chip.dataset.size, 10) });
        if (state.viewMode !== 'grid') applyViewMode('grid');
      }
    });
  }

  if (gridGapPresets) {
    gridGapPresets.addEventListener('click', (e) => {
      const chip = e.target.closest('.preset-chip');
      if (chip && chip.dataset.gap) {
        applyGridConfig({ gap: parseInt(chip.dataset.gap, 10) });
        if (state.viewMode !== 'grid') applyViewMode('grid');
      }
    });
  }

  if (gridPaddingPresets) {
    gridPaddingPresets.addEventListener('click', (e) => {
      const chip = e.target.closest('.preset-chip');
      if (chip && chip.dataset.cardPadding) {
        applyGridConfig({ cardPadding: parseInt(chip.dataset.cardPadding, 10) });
        if (state.viewMode !== 'grid') applyViewMode('grid');
      }
    });
  }

  if (resetGridSettingsBtn) {
    resetGridSettingsBtn.addEventListener('click', () => {
      applyGridConfig({
        size: GRID_BOUNDS.size.default,
        gap: GRID_BOUNDS.gap.default,
        cardPadding: GRID_BOUNDS.cardPadding.default,
      });
      showToast('Grid settings reset to default');
    });
  }

  // Sorting
  sortSelect.addEventListener('change', (e) => {
    state.sort = e.target.value;
    triggerSearchDebounced(50);
  });

  sortDirectionBtn.addEventListener('click', () => {
    state.ascending = state.ascending === 1 ? 0 : 1;
    sortDirectionBtn.textContent = state.ascending === 1 ? '⬆️' : '⬇️';
    triggerSearchDebounced(50);
  });

  // Table Header Sort Clicks
  document.querySelectorAll('.files-table th').forEach(th => {
    th.addEventListener('click', () => {
      const sortCol = th.dataset.sort;
      if (state.sort === sortCol) {
        state.ascending = state.ascending === 1 ? 0 : 1;
      } else {
        state.sort = sortCol;
        state.ascending = 1;
      }
      sortSelect.value = state.sort;
      sortDirectionBtn.textContent = state.ascending === 1 ? '⬆️' : '⬇️';
      triggerSearchDebounced(50);
    });
  });

  // Pagination Controls
  prevPageBtn.addEventListener('click', () => {
    if (state.offset > 0) {
      state.offset = Math.max(0, state.offset - state.count);
      executeSearch();
      window.scrollTo({ top: 0, behavior: 'smooth' });
    }
  });

  nextPageBtn.addEventListener('click', () => {
    if (state.offset + state.count < state.totalResults) {
      state.offset += state.count;
      executeSearch();
      window.scrollTo({ top: 0, behavior: 'smooth' });
    }
  });

  // Inspector Actions
  inspectorPreviewBtn.addEventListener('click', () => {
    if (state.selectedIndex >= 0) openPreview(state.selectedIndex);
  });

  inspectorDownloadBtn.addEventListener('click', () => {
    if (state.selectedIndex >= 0) triggerDownload(state.results[state.selectedIndex]);
  });

  inspectorCopyPathBtn.addEventListener('click', () => {
    if (state.selectedIndex >= 0) {
      copyToClipboard(state.results[state.selectedIndex].fullPath, 'Windows path copied');
    }
  });

  closeInspectorBtn.addEventListener('click', () => {
    inspectorSidebar.style.display = 'none';
  });

  // Theme Toggle
  themeToggleBtn.addEventListener('click', () => {
    const newTheme = state.theme === 'dark' ? 'light' : 'dark';
    applyTheme(newTheme);
  });

  function applyTheme(theme) {
    state.theme = theme;
    localStorage.setItem('everything_theme', theme);
    document.documentElement.setAttribute('data-theme', theme);
    themeToggleBtn.textContent = theme === 'dark' ? '🌙' : '☀️';
  }

  // Keyboard Shortcuts Modal
  helpModalBtn.addEventListener('click', () => shortcutsModal.classList.add('active'));
  closeShortcutsBtn.addEventListener('click', () => shortcutsModal.classList.remove('active'));
  shortcutsModal.addEventListener('click', (e) => {
    if (e.target === shortcutsModal) shortcutsModal.classList.remove('active');
  });

  // Logout
  logoutBtn.addEventListener('click', async () => {
    try {
      await fetch('/api/auth/logout');
      window.location.href = '/login';
    } catch (err) {
      window.location.href = '/login';
    }
  });

  // Global Keyboard Navigation & Spacebar QuickLook
  window.addEventListener('keydown', (e) => {
    // If Grid Settings Popover is open and user presses Escape, dismiss it and return focus
    if (gridSettingsPopover && !gridSettingsPopover.hasAttribute('hidden')) {
      if (e.key === 'Escape') {
        e.preventDefault();
        closeGridSettings();
        if (gridSettingsBtn) gridSettingsBtn.focus();
        return;
      }
    }

    // If inside text input (e.g. search input or any text area)
    if (['INPUT', 'TEXTAREA'].includes(document.activeElement.tagName)) {
      if (document.activeElement.type === 'range') {
        // Range slider input handles arrows / adjustments natively
        return;
      }
      if (e.key === 'Escape') {
        document.activeElement.blur();
      }
      return;
    }

    // Modal Active Keys
    if (previewModal.classList.contains('active')) {
      if (e.key === 'Escape') {
        e.preventDefault();
        closePreview();
        return;
      }
      if (e.code === 'Space') {
        // If focused on an interactive control (button, video, audio) or speed pill, allow native interaction
        if (['BUTTON', 'SELECT', 'VIDEO', 'AUDIO'].includes(document.activeElement.tagName) ||
            (document.activeElement && document.activeElement.closest && document.activeElement.closest('.video-speed-controls'))) {
          return;
        }
        e.preventDefault();
        closePreview();
        return;
      }
      if (e.key === 'ArrowLeft') {
        if (document.activeElement.tagName === 'VIDEO' || document.activeElement.tagName === 'AUDIO') {
          return; // Let video/audio seek natively
        }
        navigatePreview(-1);
        return;
      }
      if (e.key === 'ArrowRight') {
        if (document.activeElement.tagName === 'VIDEO' || document.activeElement.tagName === 'AUDIO') {
          return; // Let video/audio seek natively
        }
        navigatePreview(1);
        return;
      }
      // Speed adjustments with [ and ] when previewing video
      if ((e.key === '[' || e.key === ']') && !e.ctrlKey && !e.altKey && !e.metaKey) {
        const vid = modalBody.querySelector('video');
        if (vid) {
          e.preventDefault();
          const speeds = [0.5, 1.0, 1.25, 1.5, 2.0];
          let currentRate = vid.playbackRate;
          let idx = speeds.findIndex(s => Math.abs(s - currentRate) < 0.05);
          if (idx === -1) idx = 1;
          if (e.key === '[') idx = Math.max(0, idx - 1);
          else idx = Math.min(speeds.length - 1, idx + 1);
          vid.playbackRate = speeds[idx];
          showToast(`Playback speed: ${speeds[idx]}x`);
          return;
        }
      }
      return;
    }

    if (shortcutsModal.classList.contains('active')) {
      if (e.key === 'Escape') shortcutsModal.classList.remove('active');
      return;
    }

    // If Grid Settings Popover is open, suppress background shortcuts (Space, G, arrow keys)
    if (gridSettingsPopover && !gridSettingsPopover.hasAttribute('hidden')) {
      if (['ArrowDown', 'ArrowUp', 'ArrowLeft', 'ArrowRight', 'Space', 'g', 'G'].includes(e.key) || e.code === 'Space') {
        return;
      }
    }

    // If focused on a button or select, Space natively activates the control - do not hijack for QuickLook
    if (['BUTTON', 'SELECT'].includes(document.activeElement.tagName)) {
      if (e.code === 'Space') {
        return;
      }
    }

    // Spacebar QuickLook / Peek
    if (e.code === 'Space') {
      e.preventDefault();
      if (state.selectedIndex >= 0) {
        openPreview(state.selectedIndex);
      }
      return;
    }

    // Focus Search
    if ((e.ctrlKey && e.key.toLowerCase() === 'k') || e.key === '/') {
      e.preventDefault();
      searchInput.focus();
      searchInput.select();
      return;
    }

    // Toggle View Mode (G)
    if (e.key.toLowerCase() === 'g' && !e.ctrlKey) {
      e.preventDefault();
      applyViewMode(state.viewMode === 'grid' ? 'table' : 'grid');
      return;
    }

    // Adjust Grid Card Size ([ / ])
    if ((e.key === '[' || e.key === ']') && !e.ctrlKey && !e.altKey && !e.metaKey) {
      e.preventDefault();
      const delta = e.key === '[' ? -20 : 20;
      const newSize = clampGridValue(state.gridConfig.size + delta, GRID_BOUNDS.size);
      applyGridConfig({ size: newSize });
      if (state.viewMode !== 'grid') applyViewMode('grid');
      showToast(`Grid card size: ${newSize}px`);
      return;
    }

    // Navigation Arrows
    if (['ArrowDown', 'ArrowUp', 'ArrowLeft', 'ArrowRight'].includes(e.key)) {
      e.preventDefault();
      let perRow = 1;
      if (state.viewMode === 'grid') {
        const cards = mediaGrid.querySelectorAll('.media-card');
        if (cards.length > 1) {
          const firstTop = cards[0].offsetTop;
          let cols = 0;
          for (let i = 0; i < cards.length; i++) {
            if (cards[i].offsetTop === firstTop) cols++;
            else break;
          }
          perRow = cols > 0 ? cols : 4;
        } else {
          perRow = 4;
        }
      }
      let nextIndex = state.selectedIndex;

      if (state.selectedIndex < 0) {
        if (e.key === 'ArrowDown' || e.key === 'ArrowRight') {
          nextIndex = 0;
        } else if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') {
          nextIndex = Math.max(0, state.results.length - 1);
        }
      } else {
        if (e.key === 'ArrowDown') nextIndex += perRow;
        else if (e.key === 'ArrowUp') nextIndex -= perRow;
        else if (e.key === 'ArrowRight') nextIndex += 1;
        else if (e.key === 'ArrowLeft') nextIndex -= 1;
      }

      if (nextIndex >= 0 && nextIndex < state.results.length) {
        selectItem(nextIndex);
        scrollSelectedIntoView();
      }
      return;
    }

    // Backspace or Alt+Up to navigate up a folder
    if (e.key === 'Backspace' || (e.altKey && e.key === 'ArrowUp')) {
      if (state.currentFolder) {
        e.preventDefault();
        goUpOneFolder();
        return;
      }
    }

    // Enter to Open / Preview / Browse Folder
    if (e.key === 'Enter') {
      if (state.selectedIndex >= 0) {
        const item = state.results[state.selectedIndex];
        if (item && (item.isFolder || item.type === 'folder')) {
          navigateToFolder(item.fullPath);
        } else {
          openPreview(state.selectedIndex);
        }
      }
      return;
    }

    // '?' for Shortcuts
    if (e.key === '?' && !e.ctrlKey) {
      shortcutsModal.classList.add('active');
    }
  });

  function scrollSelectedIntoView() {
    const sel = document.querySelector('.media-card.selected, .files-table tr.selected');
    if (sel) {
      sel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
  }

  // Utility Functions
  function triggerDownload(item) {
    const a = document.createElement('a');
    a.href = item.downloadUrl;
    a.download = item.name;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    showToast(`Downloading: ${item.name}`);
  }

  function copyToClipboard(text, message) {
    navigator.clipboard.writeText(text).then(() => {
      showToast(message || 'Copied to clipboard');
    }).catch(() => {
      showToast('Could not copy to clipboard');
    });
  }

  function showToast(msg) {
    let toast = document.getElementById('appToast');
    if (!toast) {
      toast = document.createElement('div');
      toast.id = 'appToast';
      toast.style.cssText = `
        position: fixed;
        bottom: 2rem;
        right: 2rem;
        background: rgba(15, 23, 42, 0.95);
        border: 1px solid var(--accent-primary);
        color: white;
        padding: 0.75rem 1.25rem;
        border-radius: var(--radius-md);
        box-shadow: var(--shadow-lg);
        font-size: 0.85rem;
        font-weight: 500;
        z-index: 200;
        transition: opacity 0.3s ease;
      `;
      document.body.appendChild(toast);
    }
    toast.textContent = msg;
    toast.style.opacity = '1';
    clearTimeout(toast._timeout);
    toast._timeout = setTimeout(() => {
      toast.style.opacity = '0';
    }, 3000);
  }

  function getCategoryEmoji(cat) {
    switch (cat) {
      case 'image': return '🖼️';
      case 'video': return '🎬';
      case 'audio': return '🎵';
      case 'document': return '📄';
      case 'code': return '💻';
      case 'archive': return '📦';
      case 'folder': return '📁';
      default: return '📄';
    }
  }

  function escapeHtml(str) {
    if (!str) return '';
    return str
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }
});
