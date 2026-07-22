import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import type { CSSProperties, FormEvent, PointerEvent as ReactPointerEvent, WheelEvent as ReactWheelEvent } from 'react'
import './App.css'

type Asset = {
  id: string
  folderId: string
  folderName: string
  relativePath: string
  fileName: string
  contentType: string
  length: number
  createdAtUtc: string
  modifiedAtUtc: string
  averageColor: string
  viewUrl: string
  downloadUrl: string
  lowPreviewUrl: string
  previewUrl: string
}

type SourceFolder = {
  id: string
  name: string
  path: string | null
  isDefault: boolean
  isAvailable: boolean
  addedAtUtc: string
  subfolders: SourceSubfolder[]
}

type SourceSubfolder = {
  name: string
  relativePath: string
}

type SystemCapabilities = {
  isHostConnection: boolean
  nativeFolderPicker: boolean
}

type SemanticJobStatus = {
  state: string
  startedAtUtc: string | null
  finishedAtUtc: string | null
  total: number
  processed: number
  indexed: number
  skipped: number
  failed: number
  currentFile: string | null
  error: string | null
  cancellationPending: boolean
  imagesPerSecond: number
  estimatedSecondsRemaining: number | null
}

type SemanticStatus = {
  enabled: boolean
  runtimeAvailable: boolean
  modelInstalled: boolean
  modelDownloadAvailable: boolean
  modelId: string
  modelVersion: string | null
  computeSelection: string
  activeDevice: string | null
  fallbackReason: string | null
  eligible: number
  indexed: number
  stale: number
  failed: number
  remaining: number
  coveragePercent: number
  downloadState: string
  downloadProgressPercent: number
  downloadError: string | null
  job: SemanticJobStatus
}

type SemanticDevice = {
  id: string
  name: string
  kind: string
  isAvailable: boolean
}

type SearchResponse = {
  items: Asset[]
  nextCursor: string | null
  total: number
  semanticParticipated: boolean
  indexed: number
  eligible: number
}

type SearchView = SearchResponse & {
  kind: 'query' | 'similar'
  label: string
}

type ProblemDetails = {
  title?: string
  detail?: string
}

type Theme = 'light' | 'dark'
type LibraryView = 'timeline' | 'folders'
type AlbumNode = {
  key: string
  name: string
  location: string
  depth: number
  folder: SourceFolder
  assets: Asset[]
  children: AlbumNode[]
}
const LOAD_BATCH_SIZE = 24

function getInitialTheme(): Theme {
  try {
    const savedTheme = window.localStorage.getItem('gluj-drive-theme')

    if (savedTheme === 'light' || savedTheme === 'dark') {
      return savedTheme
    }
  } catch {
    // Local storage may be unavailable in privacy-restricted browsers.
  }

  return window.matchMedia('(prefers-color-scheme: dark)').matches
    ? 'dark'
    : 'light'
}

async function getErrorMessage(response: Response) {
  try {
    const problem = (await response.json()) as ProblemDetails
    return problem.detail ?? problem.title ?? 'The request could not be completed.'
  } catch {
    return 'The server returned an unexpected response.'
  }
}

function formatBytes(bytes: number) {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  return (bytes / (1024 * 1024)).toFixed(1) + ' MB'
}

function getDateKey(value: string) {
  const date = new Date(value)
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`
}

function formatMonth(value: string, short = false) {
  return new Date(value).toLocaleDateString(undefined, {
    month: short ? 'short' : 'long',
    year: 'numeric',
  })
}

function groupAssetsByMonth(assets: Asset[]) {
  const groups = new Map<string, Asset[]>()
  assets.forEach((asset) => {
    const key = getDateKey(asset.modifiedAtUtc)
    groups.set(key, [...(groups.get(key) ?? []), asset])
  })
  return [...groups.entries()]
}

function formatAssetLocation(asset: Asset) {
  return `${asset.folderName} / ${asset.relativePath}`
}

function buildAlbumTree(folder: SourceFolder, assets: Asset[]): AlbumNode {
  const root: AlbumNode = {
    key: folder.id,
    name: folder.name,
    location: folder.name,
    depth: 0,
    folder,
    assets: [],
    children: [],
  }

  assets.forEach((asset) => {
    const directoryParts = asset.relativePath.split('/').slice(0, -1).filter(Boolean)
    let current = root

    directoryParts.forEach((part, index) => {
      const location = [folder.name, ...directoryParts.slice(0, index + 1)].join(' / ')
      let child = current.children.find((candidate) => candidate.name === part)

      if (!child) {
        child = {
          key: `${folder.id}:${directoryParts.slice(0, index + 1).join('/')}`,
          name: part,
          location,
          depth: index + 1,
          folder,
          assets: [],
          children: [],
        }
        current.children.push(child)
      }

      current = child
    })

    current.assets.push(asset)
  })

  const sortNode = (node: AlbumNode) => {
    node.assets.sort((left, right) =>
      new Date(right.modifiedAtUtc).getTime() - new Date(left.modifiedAtUtc).getTime())
    node.children.sort((left, right) => left.name.localeCompare(right.name))
    node.children.forEach(sortNode)
  }
  sortNode(root)
  return root
}

function countAlbumAssets(node: AlbumNode): number {
  return node.assets.length + node.children.reduce((total, child) => total + countAlbumAssets(child), 0)
}

function ProgressivePhoto({ asset, onOpen }: { asset: Asset; onOpen: (asset: Asset) => void }) {
  const [previewStage, setPreviewStage] = useState<'color' | 'low' | 'medium'>('color')
  const cardRef = useRef<HTMLElement>(null)

  useEffect(() => {
    const card = cardRef.current
    if (!card) return

    let lowTimer: number | undefined
    let mediumTimer: number | undefined

    const clearPromotionTimers = () => {
      if (lowTimer) window.clearTimeout(lowTimer)
      if (mediumTimer) window.clearTimeout(mediumTimer)
    }

    const observer = new IntersectionObserver(
      ([entry]) => {
        clearPromotionTimers()

        if (!entry.isIntersecting) {
          setPreviewStage('color')
          return
        }

        lowTimer = window.setTimeout(() => setPreviewStage('low'), 150)
        mediumTimer = window.setTimeout(() => setPreviewStage('medium'), 500)
      },
      { rootMargin: '900px 0px', threshold: 0.01 },
    )

    observer.observe(card)
    return () => {
      clearPromotionTimers()
      observer.disconnect()
    }
  }, [asset.id])

  const previewUrl = previewStage === 'medium'
    ? asset.previewUrl
    : previewStage === 'low'
      ? asset.lowPreviewUrl
      : null

  return (
    <article className="photo-card" ref={cardRef} style={{ '--average-color': asset.averageColor } as CSSProperties}>
      <button className="photo-link" type="button" onClick={() => onOpen(asset)} aria-label={'View ' + asset.fileName}>
        {previewUrl && (
          <img
            src={previewUrl}
            alt={asset.fileName}
            loading="lazy"
            decoding="async"
            onError={() => setPreviewStage('color')}
          />
        )}
      </button>
      <div className="photo-details">
        <div>
          <strong title={asset.fileName}>{asset.fileName}</strong>
          <span>{formatBytes(asset.length)} · {new Date(asset.modifiedAtUtc).toLocaleDateString()}</span>
          <small className="asset-location" title={formatAssetLocation(asset)}>{formatAssetLocation(asset)}</small>
        </div>
        <div className="card-actions">
          <a href={asset.downloadUrl}>Download</a>
          <button className="danger-button" type="button" onClick={() => onOpen(asset)}>Delete</button>
        </div>
      </div>
    </article>
  )
}

function AlbumSection({
  node,
  collapsedKeys,
  isHostConnection,
  onToggle,
  onOpen,
  onEmpty,
}: {
  node: AlbumNode
  collapsedKeys: string[]
  isHostConnection: boolean
  onToggle: (key: string) => void
  onOpen: (asset: Asset) => void
  onEmpty: (folder: SourceFolder) => void
}) {
  const isCollapsed = collapsedKeys.includes(node.key)
  const assetCount = countAlbumAssets(node)

  return (
    <section className="album-node" style={{ '--album-indent': node.depth === 0 ? '0px' : '24px' } as CSSProperties}>
      <div className="folder-group-heading">
        <button className="folder-toggle" type="button" onClick={() => onToggle(node.key)} aria-expanded={!isCollapsed}>
          <span className="collapse-icon" aria-hidden="true" />
          <span>
            <strong>{node.name}</strong>
            <small>{node.location}</small>
          </span>
        </button>
        <div className="folder-heading-actions">
          <span>{assetCount} {assetCount === 1 ? 'picture' : 'pictures'}</span>
          {node.depth === 0 && isHostConnection && (
            <button className="text-button danger-button" type="button" onClick={() => onEmpty(node.folder)}>Empty folder</button>
          )}
        </div>
      </div>

      {!isCollapsed && (
        <div className="album-contents">
          {groupAssetsByMonth(node.assets).map(([dateKey, datedAssets]) => (
            <section className="date-group" key={dateKey}>
              <h3>{formatMonth(datedAssets[0].modifiedAtUtc)}</h3>
              <div className="photo-grid">
                {datedAssets.map((asset) => <ProgressivePhoto asset={asset} onOpen={onOpen} key={asset.id} />)}
              </div>
            </section>
          ))}
          {node.children.map((child) => (
            <AlbumSection
              node={child}
              collapsedKeys={collapsedKeys}
              isHostConnection={isHostConnection}
              onToggle={onToggle}
              onOpen={onOpen}
              onEmpty={onEmpty}
              key={child.key}
            />
          ))}
        </div>
      )}
    </section>
  )
}

function ImageViewer({
  asset,
  assets,
  onClose,
  onSelect,
  onDelete,
  onFindSimilar,
  canFindSimilar,
}: {
  asset: Asset
  assets: Asset[]
  onClose: () => void
  onSelect: (asset: Asset) => void
  onDelete: (asset: Asset) => Promise<void>
  onFindSimilar: (asset: Asset) => Promise<void>
  canFindSimilar: boolean
}) {
  const [scale, setScale] = useState(1)
  const [position, setPosition] = useState({ x: 0, y: 0 })
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [isDeleting, setIsDeleting] = useState(false)
  const scaleRef = useRef(1)
  const pointers = useRef(new Map<number, { x: number; y: number }>())
  const backgroundPointers = useRef(new Set<number>())
  const completedOnBackground = useRef(false)
  const lastPinchDistance = useRef<number | null>(null)
  const currentIndex = assets.findIndex((candidate) => candidate.id === asset.id)

  const resetView = useCallback(() => {
    scaleRef.current = 1
    setScale(1)
    setPosition({ x: 0, y: 0 })
  }, [])

  const moveTo = useCallback((nextIndex: number) => {
    if (nextIndex < 0 || nextIndex >= assets.length) return
    resetView()
    setConfirmDelete(false)
    onSelect(assets[nextIndex])
  }, [assets, onSelect, resetView])

  useEffect(() => {
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose()
      if (!confirmDelete && event.key === 'ArrowLeft') moveTo(currentIndex - 1)
      if (!confirmDelete && event.key === 'ArrowRight') moveTo(currentIndex + 1)
    }

    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.body.style.overflow = previousOverflow
      document.removeEventListener('keydown', handleKeyDown)
    }
  }, [confirmDelete, currentIndex, moveTo, onClose])

  const changeScale = (nextScale: number) => {
    const clamped = Math.min(6, Math.max(1, nextScale))
    scaleRef.current = clamped
    setScale(clamped)
    if (clamped === 1) setPosition({ x: 0, y: 0 })
  }

  const handleWheel = (event: ReactWheelEvent<HTMLDivElement>) => {
    event.preventDefault()
    changeScale(scaleRef.current * (event.deltaY < 0 ? 1.12 : 0.89))
  }

  const handlePointerDown = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (event.target === event.currentTarget) {
      backgroundPointers.current.add(event.pointerId)
    }
    event.currentTarget.setPointerCapture(event.pointerId)
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
  }

  const handlePointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const previous = pointers.current.get(event.pointerId)
    if (!previous) return

    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    const activePointers = [...pointers.current.values()]

    if (activePointers.length === 2) {
      const distance = Math.hypot(
        activePointers[0].x - activePointers[1].x,
        activePointers[0].y - activePointers[1].y,
      )
      if (lastPinchDistance.current) {
        changeScale(scaleRef.current * distance / lastPinchDistance.current)
      }
      lastPinchDistance.current = distance
    } else if (activePointers.length === 1 && scaleRef.current > 1) {
      setPosition((current) => ({
        x: current.x + event.clientX - previous.x,
        y: current.y + event.clientY - previous.y,
      }))
    }
  }

  const handlePointerEnd = (event: ReactPointerEvent<HTMLDivElement>) => {
    completedOnBackground.current = backgroundPointers.current.has(event.pointerId)
    backgroundPointers.current.delete(event.pointerId)
    pointers.current.delete(event.pointerId)
    lastPinchDistance.current = null
  }

  const deleteAsset = async () => {
    setIsDeleting(true)
    try {
      await onDelete(asset)
    } finally {
      setIsDeleting(false)
    }
  }

  return (
    <div className="viewer-backdrop" role="dialog" aria-modal="true" aria-label={'Viewing ' + asset.fileName}>
      <div className="viewer-toolbar">
        <div className="viewer-title">
          <strong>{asset.fileName}</strong>
          <span>{currentIndex + 1} of {assets.length}</span>
          <small title={formatAssetLocation(asset)}>{formatAssetLocation(asset)}</small>
        </div>
        <div className="viewer-controls">
          <button type="button" onClick={() => changeScale(scaleRef.current / 1.25)} aria-label="Zoom out">−</button>
          <button type="button" onClick={resetView}>{Math.round(scale * 100)}%</button>
          <button type="button" onClick={() => changeScale(scaleRef.current * 1.25)} aria-label="Zoom in">+</button>
          <a href={asset.downloadUrl}>Download</a>
          <button type="button" onClick={() => void onFindSimilar(asset)} disabled={!canFindSimilar}>Find similar</button>
          <button className="viewer-delete" type="button" onClick={() => setConfirmDelete(true)}>Delete</button>
          <button type="button" onClick={onClose} aria-label="Close viewer">Close</button>
        </div>
      </div>

      <button
        className="viewer-nav viewer-previous"
        type="button"
        disabled={currentIndex <= 0}
        onClick={() => moveTo(currentIndex - 1)}
        aria-label="Previous picture"
      >‹</button>
      <div
        className="viewer-stage"
        onClick={(event) => {
          if (event.target === event.currentTarget && completedOnBackground.current) {
            onClose()
          }
          completedOnBackground.current = false
        }}
        onWheel={handleWheel}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerEnd}
        onPointerCancel={handlePointerEnd}
        onDoubleClick={() => changeScale(scaleRef.current > 1 ? 1 : 2)}
      >
        <img
          src={asset.viewUrl}
          alt={asset.fileName}
          draggable="false"
          style={{ transform: `translate3d(${position.x}px, ${position.y}px, 0) scale(${scale})` }}
        />
      </div>
      <button
        className="viewer-nav viewer-next"
        type="button"
        disabled={currentIndex >= assets.length - 1}
        onClick={() => moveTo(currentIndex + 1)}
        aria-label="Next picture"
      >›</button>

      {confirmDelete && (
        <div className="confirm-card" role="alertdialog" aria-modal="true" aria-labelledby="delete-picture-title">
          <p className="eyebrow">Move to trash</p>
          <h2 id="delete-picture-title">Delete this picture?</h2>
          <p><strong>{asset.fileName}</strong> will be moved to the folder’s <code>.gluj-trash</code> directory.</p>
          <div className="confirm-actions">
            <button type="button" onClick={() => setConfirmDelete(false)} disabled={isDeleting}>Keep picture</button>
            <button className="destructive-action" type="button" onClick={() => void deleteAsset()} disabled={isDeleting}>
              {isDeleting ? 'Deleting…' : 'Move to trash'}
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

function App() {
  const [assets, setAssets] = useState<Asset[]>([])
  const [folders, setFolders] = useState<SourceFolder[]>([])
  const [selectedFolderId, setSelectedFolderId] = useState('')
  const [selectedUploadPath, setSelectedUploadPath] = useState('')
  const [isLoading, setIsLoading] = useState(true)
  const [isUploading, setIsUploading] = useState(false)
  const [uploadCount, setUploadCount] = useState(0)
  const [isUpdatingFolders, setIsUpdatingFolders] = useState(false)
  const [isPickingFolder, setIsPickingFolder] = useState(false)
  const [capabilities, setCapabilities] = useState<SystemCapabilities | null>(null)
  const [showFolderManager, setShowFolderManager] = useState(false)
  const [showAiManager, setShowAiManager] = useState(false)
  const [semanticStatus, setSemanticStatus] = useState<SemanticStatus | null>(null)
  const [semanticDevices, setSemanticDevices] = useState<SemanticDevice[]>([])
  const [isAiActionPending, setIsAiActionPending] = useState(false)
  const [newFolderPath, setNewFolderPath] = useState('')
  const [newFolderName, setNewFolderName] = useState('')
  const [makeNewFolderDefault, setMakeNewFolderDefault] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [theme, setTheme] = useState<Theme>(getInitialTheme)
  const [libraryView, setLibraryView] = useState<LibraryView>('timeline')
  const [searchQuery, setSearchQuery] = useState('')
  const [searchView, setSearchView] = useState<SearchView | null>(null)
  const [isSearching, setIsSearching] = useState(false)
  const [visibleCount, setVisibleCount] = useState(LOAD_BATCH_SIZE)
  const [collapsedFolderIds, setCollapsedFolderIds] = useState<string[]>([])
  const [viewerAsset, setViewerAsset] = useState<Asset | null>(null)
  const [folderPendingEmpty, setFolderPendingEmpty] = useState<SourceFolder | null>(null)
  const [isEmptyingFolder, setIsEmptyingFolder] = useState(false)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const loadMoreRef = useRef<HTMLDivElement>(null)
  const searchRequestRef = useRef(0)

  useLayoutEffect(() => {
    document.documentElement.dataset.theme = theme

    try {
      window.localStorage.setItem('gluj-drive-theme', theme)
    } catch {
      // The selected theme still applies for the current session.
    }
  }, [theme])

  const loadLibrary = useCallback(async (signal?: AbortSignal) => {
    setIsLoading(true)

    try {
      const [assetsResponse, foldersResponse, capabilitiesResponse] = await Promise.all([
        fetch('/api/assets', { signal }),
        fetch('/api/folders', { signal }),
        fetch('/api/system/capabilities', { signal }),
      ])

      if (!assetsResponse.ok) {
        throw new Error(await getErrorMessage(assetsResponse))
      }

      if (!foldersResponse.ok) {
        throw new Error(await getErrorMessage(foldersResponse))
      }

      if (!capabilitiesResponse.ok) {
        throw new Error(await getErrorMessage(capabilitiesResponse))
      }

      const loadedAssets = (await assetsResponse.json()) as Asset[]
      const loadedFolders = (await foldersResponse.json()) as SourceFolder[]
      const loadedCapabilities = (await capabilitiesResponse.json()) as SystemCapabilities
      setAssets(loadedAssets)
      setFolders(loadedFolders)
      setCapabilities(loadedCapabilities)
      setSelectedFolderId((current) => {
        const currentFolder = loadedFolders.find(
          (folder) => folder.id === current && folder.isAvailable,
        )
        const nextFolder = currentFolder ??
          loadedFolders.find((folder) => folder.isDefault && folder.isAvailable) ??
          loadedFolders.find((folder) => folder.isAvailable)
        setSelectedUploadPath((currentPath) =>
          nextFolder?.subfolders.some((subfolder) => subfolder.relativePath === currentPath)
            ? currentPath
            : '')
        return (
          nextFolder?.id ?? ''
        )
      })
      setError(null)
    } catch (caughtError: unknown) {
      if (caughtError instanceof DOMException && caughtError.name === 'AbortError') {
        return
      }

      setError(
        caughtError instanceof Error
          ? caughtError.message
          : 'Could not connect to the photo server.',
      )
    } finally {
      if (!signal?.aborted) {
        setIsLoading(false)
      }
    }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void loadLibrary(controller.signal)
    return () => controller.abort()
  }, [loadLibrary])

  const loadAiStatus = useCallback(async (signal?: AbortSignal) => {
    const [statusResponse, devicesResponse] = await Promise.all([
      fetch('/api/ai/status', { signal }),
      fetch('/api/ai/devices', { signal }),
    ])

    if (!statusResponse.ok) throw new Error(await getErrorMessage(statusResponse))
    if (!devicesResponse.ok) throw new Error(await getErrorMessage(devicesResponse))
    setSemanticStatus((await statusResponse.json()) as SemanticStatus)
    setSemanticDevices((await devicesResponse.json()) as SemanticDevice[])
  }, [])

  useEffect(() => {
    if (!showAiManager || !capabilities?.isHostConnection) return
    const controller = new AbortController()
    void loadAiStatus(controller.signal).catch((caughtError: unknown) => {
      if (!(caughtError instanceof DOMException && caughtError.name === 'AbortError')) {
        setError(caughtError instanceof Error ? caughtError.message : 'Could not load AI search status.')
      }
    })
    const timer = window.setInterval(() => {
      void loadAiStatus(controller.signal).catch(() => undefined)
    }, 1500)
    return () => {
      controller.abort()
      window.clearInterval(timer)
    }
  }, [capabilities?.isHostConnection, loadAiStatus, showAiManager])

  const runAiAction = async (path: string, init?: RequestInit) => {
    setIsAiActionPending(true)
    setError(null)
    try {
      const response = await fetch(path, init)
      if (!response.ok) throw new Error(await getErrorMessage(response))
      await loadAiStatus()
    } catch (caughtError: unknown) {
      setError(caughtError instanceof Error ? caughtError.message : 'The AI search operation failed.')
    } finally {
      setIsAiActionPending(false)
    }
  }

  const startAnalysis = async (reanalyzeAll: boolean) => {
    if (reanalyzeAll && !window.confirm('Reanalyze every image? Existing semantic vectors will be replaced.')) return
    await runAiAction('/api/ai/analysis', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ reanalyzeAll }),
    })
  }

  const updateComputeSelection = async (computeSelection: string) => {
    await runAiAction('/api/ai/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ computeSelection }),
    })
  }

  const uploadFiles = async (files: File[]) => {
    if (files.length === 0 || !selectedFolderId) {
      setError('Select an available upload folder and at least one image.')
      return
    }

    const formData = new FormData()
    files.forEach((file) => formData.append('files', file))

    setIsUploading(true)
    setUploadCount(files.length)
    setError(null)

    try {
      const response = await fetch(
        '/api/uploads?folderId=' + encodeURIComponent(selectedFolderId) +
          (selectedUploadPath ? '&relativePath=' + encodeURIComponent(selectedUploadPath) : ''),
        {
          method: 'POST',
          body: formData,
        },
      )

      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      await loadLibrary()
    } catch (caughtError: unknown) {
      setError(
        caughtError instanceof Error
          ? caughtError.message
          : 'The images could not be uploaded.',
      )
    } finally {
      setIsUploading(false)
      setUploadCount(0)

      if (fileInputRef.current) {
        fileInputRef.current.value = ''
      }
    }
  }

  const addFolder = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()

    if (!newFolderPath.trim()) {
      setError('Enter a folder path from the server computer.')
      return
    }

    setIsUpdatingFolders(true)
    setError(null)

    try {
      const response = await fetch('/api/folders', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          path: newFolderPath,
          name: newFolderName || null,
          makeDefault: makeNewFolderDefault,
        }),
      })

      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      setNewFolderPath('')
      setNewFolderName('')
      setMakeNewFolderDefault(false)
      await loadLibrary()
    } catch (caughtError: unknown) {
      setError(
        caughtError instanceof Error
          ? caughtError.message
          : 'The folder could not be added.',
      )
    } finally {
      setIsUpdatingFolders(false)
    }
  }

  const pickFolder = async () => {
    setIsPickingFolder(true)
    setError(null)

    try {
      const response = await fetch('/api/system/folders/pick', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ initialPath: newFolderPath || null }),
      })

      if (response.status === 204) {
        return
      }

      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      const result = (await response.json()) as { path: string }
      setNewFolderPath(result.path)
    } catch (caughtError: unknown) {
      setError(
        caughtError instanceof Error
          ? caughtError.message
          : 'The native folder picker could not be opened.',
      )
    } finally {
      setIsPickingFolder(false)
    }
  }

  const setDefaultFolder = async (folderId: string) => {
    setIsUpdatingFolders(true)
    setError(null)

    try {
      const response = await fetch('/api/folders/' + folderId + '/default', {
        method: 'PUT',
      })

      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      setSelectedFolderId(folderId)
      setSelectedUploadPath('')
      await loadLibrary()
    } catch (caughtError: unknown) {
      setError(
        caughtError instanceof Error
          ? caughtError.message
          : 'The default folder could not be changed.',
      )
    } finally {
      setIsUpdatingFolders(false)
    }
  }

  const removeFolder = async (folder: SourceFolder) => {
    const confirmed = window.confirm(
      'Stop scanning "' + folder.name + '"? No files will be deleted.',
    )

    if (!confirmed) return

    setIsUpdatingFolders(true)
    setError(null)

    try {
      const response = await fetch('/api/folders/' + folder.id, {
        method: 'DELETE',
      })

      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      await loadLibrary()
    } catch (caughtError: unknown) {
      setError(
        caughtError instanceof Error
          ? caughtError.message
          : 'The folder could not be removed.',
      )
    } finally {
      setIsUpdatingFolders(false)
    }
  }

  const deleteAsset = useCallback(async (asset: Asset) => {
    setError(null)
    const response = await fetch('/api/assets/' + asset.id, { method: 'DELETE' })

    if (!response.ok) {
      const message = await getErrorMessage(response)
      setError(message)
      throw new Error(message)
    }

    setAssets((current) => current.filter((candidate) => candidate.id !== asset.id))
    setViewerAsset(null)
  }, [])

  const emptyFolder = async (folder: SourceFolder) => {
    setIsEmptyingFolder(true)
    setError(null)

    try {
      const response = await fetch('/api/folders/' + folder.id + '/assets', {
        method: 'DELETE',
      })

      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      setFolderPendingEmpty(null)
      await loadLibrary()
    } catch (caughtError: unknown) {
      setError(caughtError instanceof Error ? caughtError.message : 'The folder could not be emptied.')
    } finally {
      setIsEmptyingFolder(false)
    }
  }

  const loadSearchPage = useCallback(async (
    query: string,
    cursor: string | null,
    append: boolean,
    requestId: number,
  ) => {
    setIsSearching(true)
    try {
      const response = await fetch('/api/search', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ query, pageSize: 100, cursor }),
      })
      if (!response.ok) throw new Error(await getErrorMessage(response))
      const result = (await response.json()) as SearchResponse
      if (searchRequestRef.current !== requestId) return
      setSearchView((current) => ({
        ...result,
        items: append && current?.kind === 'query'
          ? [...current.items, ...result.items]
          : result.items,
        kind: 'query',
        label: result.semanticParticipated
          ? `Semantic search${result.eligible > 0 ? ` · ${Math.round(result.indexed * 100 / result.eligible)}% indexed` : ''}`
          : 'Filename search',
      }))
    } catch (caughtError: unknown) {
      if (searchRequestRef.current === requestId) {
        setError(caughtError instanceof Error ? caughtError.message : 'Search failed.')
      }
    } finally {
      if (searchRequestRef.current === requestId) setIsSearching(false)
    }
  }, [])

  useEffect(() => {
    const query = searchQuery.trim()
    const requestId = ++searchRequestRef.current

    if (!query) {
      setSearchView((current) => current?.kind === 'query' ? null : current)
      setIsSearching(false)
      return
    }

    setSearchView(null)
    setIsSearching(true)
    const timer = window.setTimeout(() => {
      void loadSearchPage(query, null, false, requestId)
    }, 300)
    return () => window.clearTimeout(timer)
  }, [loadSearchPage, searchQuery])

  const findSimilar = useCallback(async (asset: Asset) => {
    const requestId = ++searchRequestRef.current
    setIsSearching(true)
    setError(null)
    try {
      const response = await fetch(`/api/assets/${asset.id}/similar?limit=100`)
      if (!response.ok) throw new Error(await getErrorMessage(response))
      const result = (await response.json()) as Omit<SearchResponse, 'nextCursor' | 'total'>
      if (!result.semanticParticipated) {
        throw new Error('Analyze this picture before using Find similar.')
      }
      setSearchQuery('')
      setSearchView({
        ...result,
        nextCursor: null,
        total: result.items.length,
        kind: 'similar',
        label: `Similar to ${asset.fileName}`,
      })
      setViewerAsset(null)
      setVisibleCount(LOAD_BATCH_SIZE)
      window.scrollTo({ top: 0, behavior: 'smooth' })
    } catch (caughtError: unknown) {
      if (searchRequestRef.current === requestId) {
        setError(caughtError instanceof Error ? caughtError.message : 'Could not find similar pictures.')
      }
    } finally {
      if (searchRequestRef.current === requestId) setIsSearching(false)
    }
  }, [])

  const filteredAssets = useMemo(() => {
    if (searchView) return searchView.items
    return searchQuery.trim() ? [] : assets
  }, [assets, searchQuery, searchView])

  const orderedAssets = useMemo(() => [...filteredAssets].sort((left, right) =>
    new Date(right.modifiedAtUtc).getTime() - new Date(left.modifiedAtUtc).getTime()), [filteredAssets])
  const visibleAssets = orderedAssets.slice(0, visibleCount)
  const hasMoreAssets = visibleCount < orderedAssets.length || Boolean(searchView?.nextCursor)

  useEffect(() => {
    setVisibleCount(LOAD_BATCH_SIZE)
  }, [libraryView, searchQuery, searchView?.label])

  useEffect(() => {
    const target = loadMoreRef.current
    if (!target || !hasMoreAssets) return

    const observer = new IntersectionObserver(
      ([entry]) => {
        if (entry.isIntersecting) {
          if (visibleCount < orderedAssets.length) {
            setVisibleCount((current) => Math.min(current + LOAD_BATCH_SIZE, orderedAssets.length))
          } else if (searchView?.kind === 'query' && searchView.nextCursor && !isSearching) {
            void loadSearchPage(
              searchQuery.trim(),
              searchView.nextCursor,
              true,
              searchRequestRef.current,
            )
          }
        }
      },
      { rootMargin: '500px 0px' },
    )

    observer.observe(target)
    return () => observer.disconnect()
  }, [hasMoreAssets, isSearching, loadSearchPage, orderedAssets.length, searchQuery, searchView, visibleCount])

  const albumTrees = folders
    .map((folder) => buildAlbumTree(
      folder,
      visibleAssets.filter((asset) => asset.folderId === folder.id),
    ))
    .filter((tree) => countAlbumAssets(tree) > 0)

  const dateRailEntries = useMemo(() => {
    const seen = new Set<string>()
    return orderedAssets.flatMap((asset) => {
      const dateKey = getDateKey(asset.modifiedAtUtc)
      if (seen.has(dateKey)) return []
      seen.add(dateKey)
      return [{
        dateKey,
        date: asset.modifiedAtUtc,
      }]
    })
  }, [orderedAssets])

  const jumpToDate = (dateKey: string) => {
    const assetIndex = orderedAssets.findIndex((asset) => getDateKey(asset.modifiedAtUtc) === dateKey)
    if (assetIndex < 0) return

    setVisibleCount((current) => Math.max(current, assetIndex + LOAD_BATCH_SIZE))
    window.setTimeout(() => {
      document.getElementById(`date-${dateKey}`)?.scrollIntoView({
        behavior: 'smooth',
        block: 'start',
      })
    }, 50)
  }

  const toggleFolder = (key: string) => {
    setCollapsedFolderIds((current) => current.includes(key)
      ? current.filter((id) => id !== key)
      : [...current, key])
  }

  return (
    <main className="app-shell">
      <header className="site-header">
        <div>
          <p className="eyebrow">Personal photo server</p>
          <h1>Gluj Drive</h1>
        </div>

        <div className="header-actions">
          <button
            className="secondary-button theme-button"
            type="button"
            aria-label={'Switch to ' + (theme === 'dark' ? 'day' : 'night') + ' theme'}
            aria-pressed={theme === 'dark'}
            onClick={() => setTheme((current) => (current === 'dark' ? 'light' : 'dark'))}
          >
            <span aria-hidden="true">{theme === 'dark' ? '☀' : '☾'}</span>
            {theme === 'dark' ? 'Day' : 'Night'}
          </button>

          {capabilities?.isHostConnection && (
            <button
              className="secondary-button"
              type="button"
              onClick={() => setShowAiManager((current) => !current)}
              aria-expanded={showAiManager}
            >
              AI search
            </button>
          )}

          {capabilities?.isHostConnection && (
            <button
              className="secondary-button"
              type="button"
              onClick={() => setShowFolderManager((current) => !current)}
              aria-expanded={showFolderManager}
            >
              Folders
            </button>
          )}

          <button
            className="secondary-button"
            type="button"
            onClick={() => void loadLibrary()}
            disabled={isLoading || isUploading}
          >
            Refresh
          </button>
        </div>
      </header>

      {showAiManager && capabilities?.isHostConnection && (
        <section className="ai-manager" aria-labelledby="ai-manager-title">
          <div className="ai-manager-heading">
            <div>
              <p className="eyebrow">Local and optional</p>
              <h2 id="ai-manager-title">AI search</h2>
              <p>Analyze image pixels only when you ask. Images and searches stay on this computer.</p>
            </div>
            <span className="ai-state" data-state={semanticStatus?.job.state ?? 'idle'}>
              {semanticStatus?.job.state ?? 'Loading'}
            </span>
          </div>

          {!semanticStatus ? (
            <div className="ai-loading"><span className="loading-dot" /> Loading AI status...</div>
          ) : (
            <>
              <div className="ai-stats">
                <div><strong>{semanticStatus.indexed}</strong><span>Indexed</span></div>
                <div><strong>{semanticStatus.remaining}</strong><span>Remaining</span></div>
                <div><strong>{semanticStatus.stale}</strong><span>Stale</span></div>
                <div><strong>{semanticStatus.failed}</strong><span>Failed</span></div>
              </div>

              <div className="ai-progress" aria-label={`${semanticStatus.coveragePercent.toFixed(1)}% indexed`}>
                <span style={{ width: `${Math.min(100, semanticStatus.coveragePercent)}%` }} />
              </div>

              {semanticStatus.downloadState === 'downloading' && (
                <div className="ai-active-progress">
                  <div><span>Downloading model</span><strong>{Math.round(semanticStatus.downloadProgressPercent)}%</strong></div>
                  <div
                    className="ai-progress"
                    role="progressbar"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={Math.round(semanticStatus.downloadProgressPercent)}
                  >
                    <span style={{ width: `${Math.min(100, semanticStatus.downloadProgressPercent)}%` }} />
                  </div>
                </div>
              )}

              {semanticStatus.job.state === 'running' && (
                <div className="ai-active-progress" aria-live="polite">
                  <div>
                    <span>{semanticStatus.job.cancellationPending ? 'Finishing current image' : 'Indexing images'}</span>
                    <strong>
                      {semanticStatus.job.total > 0
                        ? Math.round(semanticStatus.job.processed * 100 / semanticStatus.job.total)
                        : 0}%
                    </strong>
                  </div>
                  <div
                    className="ai-progress"
                    role="progressbar"
                    aria-valuemin={0}
                    aria-valuemax={semanticStatus.job.total}
                    aria-valuenow={semanticStatus.job.processed}
                    aria-valuetext={`${semanticStatus.job.processed} of ${semanticStatus.job.total} images`}
                  >
                    <span style={{ width: `${semanticStatus.job.total > 0 ? Math.min(100, semanticStatus.job.processed * 100 / semanticStatus.job.total) : 0}%` }} />
                  </div>
                </div>
              )}

              <div className="ai-controls">
                <label className="folder-select">
                  <span>Compute device</span>
                  <select
                    value={semanticStatus.computeSelection}
                    disabled={isAiActionPending || semanticStatus.job.state === 'running'}
                    onChange={(event) => void updateComputeSelection(event.target.value)}
                  >
                    <option value="auto">Auto (Vulkan, then CPU)</option>
                    {semanticDevices.map((device) => (
                      <option value={device.id} disabled={!device.isAvailable} key={device.id}>
                        {device.name}{device.isAvailable ? '' : ' (unavailable)'}
                      </option>
                    ))}
                  </select>
                </label>

                {!semanticStatus.modelInstalled && (
                  <button
                    className="secondary-button"
                    type="button"
                    disabled={isAiActionPending || !semanticStatus.modelDownloadAvailable || semanticStatus.downloadState === 'downloading'}
                    onClick={() => void runAiAction('/api/ai/model/download', { method: 'POST' })}
                    title={semanticStatus.modelDownloadAvailable ? undefined : 'Configure a verified model package URL on the server first.'}
                  >
                    {semanticStatus.downloadState === 'downloading'
                      ? `Downloading ${Math.round(semanticStatus.downloadProgressPercent)}%`
                      : 'Download model'}
                  </button>
                )}

                {semanticStatus.job.state === 'running' ? (
                  <button
                    className="secondary-button danger-button"
                    type="button"
                    disabled={semanticStatus.job.cancellationPending}
                    onClick={() => void runAiAction('/api/ai/analysis/cancel', { method: 'POST' })}
                  >
                    {semanticStatus.job.cancellationPending ? 'Stopping...' : 'Cancel analysis'}
                  </button>
                ) : (
                  <>
                    <button
                      className="upload-button"
                      type="button"
                      disabled={isAiActionPending || !semanticStatus.modelInstalled || !semanticStatus.runtimeAvailable}
                      onClick={() => void startAnalysis(false)}
                    >
                      {semanticStatus.job.state === 'cancelled' ? 'Resume analysis' : 'Analyze library'}
                    </button>
                    <button
                      className="text-button"
                      type="button"
                      disabled={isAiActionPending || !semanticStatus.modelInstalled || !semanticStatus.runtimeAvailable}
                      onClick={() => void startAnalysis(true)}
                    >
                      Reanalyze all
                    </button>
                  </>
                )}
              </div>

              <div className="ai-detail">
                <span>{semanticStatus.modelInstalled ? `${semanticStatus.modelId} ${semanticStatus.modelVersion ?? ''}` : 'Model not installed'}</span>
                <span>{semanticStatus.activeDevice ?? (semanticStatus.runtimeAvailable ? 'Runtime ready' : 'Native runtime not installed')}</span>
                {semanticStatus.job.currentFile && <code title={semanticStatus.job.currentFile}>{semanticStatus.job.currentFile}</code>}
                {semanticStatus.job.state === 'running' && (
                  <span>
                    {semanticStatus.job.processed} of {semanticStatus.job.total} · {semanticStatus.job.imagesPerSecond.toFixed(1)} images/s
                    {semanticStatus.job.estimatedSecondsRemaining !== null ? ` · about ${semanticStatus.job.estimatedSecondsRemaining}s left` : ''}
                  </span>
                )}
                {(semanticStatus.fallbackReason || semanticStatus.downloadError || semanticStatus.job.error) && (
                  <span className="ai-warning">{semanticStatus.fallbackReason ?? semanticStatus.downloadError ?? semanticStatus.job.error}</span>
                )}
              </div>
            </>
          )}
        </section>
      )}

      {showFolderManager && capabilities?.isHostConnection && (
        <section className="folder-manager" aria-labelledby="folder-manager-title">
          <div className="folder-manager-heading">
            <div>
              <h2 id="folder-manager-title">Source folders</h2>
              <p>Existing images stay in place. Removing a folder only stops scanning it.</p>
            </div>
          </div>

          <form className="folder-form" onSubmit={(event) => void addFolder(event)}>
            <div className="folder-path-field">
              <label htmlFor="new-folder-path">Windows folder path</label>
              <div className="folder-path-input">
                <input
                  id="new-folder-path"
                  type="text"
                  value={newFolderPath}
                  onChange={(event) => setNewFolderPath(event.target.value)}
                  placeholder="D:\\Pictures"
                  disabled={isUpdatingFolders || isPickingFolder}
                />
                {capabilities.nativeFolderPicker && (
                  <button
                    className="secondary-button browse-button"
                    type="button"
                    onClick={() => void pickFolder()}
                    disabled={isUpdatingFolders || isPickingFolder}
                  >
                    {isPickingFolder ? 'Browsing...' : 'Browse...'}
                  </button>
                )}
              </div>
            </div>
            <label>
              <span>Display name (optional)</span>
              <input
                type="text"
                value={newFolderName}
                onChange={(event) => setNewFolderName(event.target.value)}
                placeholder="Family photos"
                disabled={isUpdatingFolders}
              />
            </label>
            <label className="checkbox-label">
              <input
                type="checkbox"
                checked={makeNewFolderDefault}
                onChange={(event) => setMakeNewFolderDefault(event.target.checked)}
                disabled={isUpdatingFolders}
              />
              Make this the default upload folder
            </label>
            <button className="upload-button add-folder-button" type="submit" disabled={isUpdatingFolders}>
              {isUpdatingFolders ? 'Updating...' : 'Add folder'}
            </button>
          </form>

          <div className="folder-list">
            {folders.map((folder) => (
              <article className="folder-row" key={folder.id}>
                <div className="folder-status" data-available={folder.isAvailable} />
                <div className="folder-copy">
                  <div>
                    <strong>{folder.name}</strong>
                    {folder.isDefault && <span className="default-badge">Default</span>}
                    {!folder.isAvailable && <span className="offline-badge">Unavailable</span>}
                  </div>
                  {folder.path && <code title={folder.path}>{folder.path}</code>}
                  {folder.subfolders.length > 0 && (
                    <div className="source-subfolders" aria-label={`${folder.name} subfolders`}>
                      {folder.subfolders.map((subfolder) => {
                        const depth = subfolder.relativePath.split('/').length
                        return (
                          <span
                            key={subfolder.relativePath}
                            style={{ '--subfolder-depth': depth } as CSSProperties}
                            title={`${folder.name} / ${subfolder.relativePath}`}
                          >
                            <i aria-hidden="true">↳</i> {subfolder.name}
                          </span>
                        )
                      })}
                    </div>
                  )}
                </div>
                <div className="folder-actions">
                  {!folder.isDefault && (
                    <button
                      className="text-button"
                      type="button"
                      disabled={isUpdatingFolders || !folder.isAvailable}
                      onClick={() => void setDefaultFolder(folder.id)}
                    >
                      Set default
                    </button>
                  )}
                  <button
                    className="text-button danger-button"
                    type="button"
                    disabled={isUpdatingFolders || !folder.isAvailable}
                    onClick={() => setFolderPendingEmpty(folder)}
                  >
                    Empty
                  </button>
                  <button
                    className="text-button danger-button"
                    type="button"
                    disabled={isUpdatingFolders}
                    onClick={() => void removeFolder(folder)}
                  >
                    Remove
                  </button>
                </div>
              </article>
            ))}
          </div>
        </section>
      )}

      <section className="intro">
        <div>
          <h2>Your library</h2>
          <p>Pictures discovered in your registered source folders.</p>
        </div>
        <span className="asset-count">
          {assets.length} {assets.length === 1 ? 'picture' : 'pictures'}
        </span>
      </section>

      <div className="library-tools">
        <label className="search-field" htmlFor="library-search">
          <span aria-hidden="true">⌕</span>
          <input
            id="library-search"
            type="search"
            value={searchQuery}
            onChange={(event) => {
              setSearchView((current) => current?.kind === 'similar' ? null : current)
              setSearchQuery(event.target.value)
            }}
            placeholder="Search filenames or what is in a picture"
          />
          {searchQuery && (
            <button type="button" onClick={() => { setSearchQuery(''); setSearchView(null) }} aria-label="Clear search">×</button>
          )}
        </label>
        <span className="result-summary">
          {isSearching
            ? 'Searching...'
            : searchView
              ? `${searchView.label} · ${searchView.total} results`
              : `${assets.length} total`}
        </span>
        <button
          className="secondary-button view-toggle"
          type="button"
          onClick={() => setLibraryView((current) => current === 'timeline' ? 'folders' : 'timeline')}
        >
          {libraryView === 'timeline' ? 'View albums' : 'View timeline'}
        </button>
      </div>

      <section className="upload-panel" aria-label="Upload pictures">
        <label className="folder-select">
          <span>Upload into</span>
          <select
            value={`${selectedFolderId}::${selectedUploadPath}`}
            onChange={(event) => {
              const separatorIndex = event.target.value.indexOf('::')
              setSelectedFolderId(event.target.value.slice(0, separatorIndex))
              setSelectedUploadPath(event.target.value.slice(separatorIndex + 2))
            }}
            disabled={isUploading}
          >
            {folders.filter((folder) => folder.isAvailable).map((folder) => (
              <optgroup label={folder.name + (folder.isDefault ? ' (default)' : '')} key={folder.id}>
                <option value={`${folder.id}::`}>
                  {folder.name} / root
                </option>
                {folder.subfolders.map((subfolder) => (
                  <option value={`${folder.id}::${subfolder.relativePath}`} key={subfolder.relativePath}>
                    {'\u00a0\u00a0'.repeat(subfolder.relativePath.split('/').length)}↳ {subfolder.name}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
        </label>

        <input
          ref={fileInputRef}
          className="file-input"
          id="photo-upload"
          type="file"
          multiple
          accept="image/jpeg,image/png,image/webp,image/gif,image/heic,image/heif,.heic,.heif"
          disabled={isUploading || !selectedFolderId}
          onChange={(event) => {
            const files = Array.from(event.target.files ?? [])
            if (files.length > 0) void uploadFiles(files)
          }}
        />
        <label
          className="upload-button"
          data-disabled={isUploading || !selectedFolderId}
          htmlFor="photo-upload"
        >
          {isUploading
            ? 'Uploading ' + uploadCount + (uploadCount === 1 ? ' picture...' : ' pictures...')
            : 'Upload pictures'}
        </label>
      </section>

      {error && (
        <div className="error-message" role="alert">
          <strong>Something went wrong</strong>
          <span>{error}</span>
        </div>
      )}

      {isLoading && assets.length === 0 ? (
        <div className="empty-state" aria-live="polite">
          <span className="loading-dot" />
          Scanning your source folders...
        </div>
      ) : assets.length === 0 ? (
        <div className="empty-state">
          <strong>No pictures found</strong>
          <span>Upload pictures or register a folder that already contains some.</span>
        </div>
      ) : isSearching && filteredAssets.length === 0 ? (
        <div className="empty-state" aria-live="polite">
          <span className="loading-dot" />
          Searching your library...
        </div>
      ) : filteredAssets.length === 0 ? (
        <div className="empty-state">
          <strong>No matching pictures</strong>
          <span>Try a different filename or description.</span>
          <button className="secondary-button" type="button" onClick={() => { setSearchQuery(''); setSearchView(null) }}>Clear search</button>
        </div>
      ) : (
        <>
        {libraryView === 'timeline' ? (
          <div className="timeline-groups">
            {groupAssetsByMonth(visibleAssets).map(([dateKey, datedAssets]) => (
              <section className="date-group" id={`date-${dateKey}`} key={dateKey}>
                <h3>{formatMonth(datedAssets[0].modifiedAtUtc)}</h3>
                <div className="photo-grid">
                  {datedAssets.map((asset) => (
                    <ProgressivePhoto asset={asset} onOpen={setViewerAsset} key={asset.id} />
                  ))}
                </div>
              </section>
            ))}
          </div>
        ) : (
          <div className="folder-groups album-tree">
            {albumTrees.map((tree) => (
              <AlbumSection
                node={tree}
                collapsedKeys={collapsedFolderIds}
                isHostConnection={capabilities?.isHostConnection ?? false}
                onToggle={toggleFolder}
                onOpen={setViewerAsset}
                onEmpty={setFolderPendingEmpty}
                key={tree.key}
              />
            ))}
          </div>
        )}
        {libraryView === 'timeline' && dateRailEntries.length > 1 && (
          <nav className="date-rail" aria-label="Jump to a month">
            <span>Jump to</span>
            <div>
              {dateRailEntries.map((entry) => (
                <button
                  type="button"
                  key={entry.dateKey}
                  title={formatMonth(entry.date)}
                  onClick={() => jumpToDate(entry.dateKey)}
                >
                  {formatMonth(entry.date, true)}
                </button>
              ))}
            </div>
          </nav>
        )}
        <div className="load-more-sentinel" ref={loadMoreRef} aria-live="polite">
          {hasMoreAssets ? (
            <><span className="loading-dot" /> Loading more pictures…</>
          ) : (
            <span>All {searchView?.total ?? filteredAssets.length} pictures are displayed.</span>
          )}
        </div>
        </>
      )}

      {viewerAsset && (
        <ImageViewer
          asset={viewerAsset}
          assets={orderedAssets}
          onClose={() => setViewerAsset(null)}
          onSelect={setViewerAsset}
          onDelete={deleteAsset}
          onFindSimilar={findSimilar}
          canFindSimilar
        />
      )}

      {folderPendingEmpty && (
        <div className="dialog-backdrop" role="dialog" aria-modal="true" aria-labelledby="empty-folder-title">
          <div className="confirm-card folder-warning">
            <p className="eyebrow">Destructive folder action</p>
            <h2 id="empty-folder-title">Empty “{folderPendingEmpty.name}”?</h2>
            <p>Every supported image in this folder and its subfolders will be moved into <code>.gluj-trash</code>. This affects the real files on the host computer.</p>
            <p className="warning-count">{assets.filter((asset) => asset.folderId === folderPendingEmpty.id).length} pictures are currently scanned.</p>
            <div className="confirm-actions">
              <button type="button" onClick={() => setFolderPendingEmpty(null)} disabled={isEmptyingFolder}>Cancel</button>
              <button className="destructive-action" type="button" onClick={() => void emptyFolder(folderPendingEmpty)} disabled={isEmptyingFolder}>
                {isEmptyingFolder ? 'Moving pictures…' : 'Yes, empty folder'}
              </button>
            </div>
          </div>
        </div>
      )}
    </main>
  )
}

export default App
