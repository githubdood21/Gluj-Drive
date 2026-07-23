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
  mediaKind: 'image' | 'animation' | 'video'
  fileExtension: string
  length: number
  createdAtUtc: string
  modifiedAtUtc: string
  averageColor: string
  pixelWidth: number | null
  pixelHeight: number | null
  viewUrl: string
  downloadUrl: string
  lowPreviewUrl: string
  previewUrl: string
  matchConfidence?: number | null
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
  isExcluded: boolean
  isDirectlyExcluded: boolean
}

type SystemCapabilities = {
  isHostConnection: boolean
  nativeFolderPicker: boolean
}

type AuthStatus = {
  isHostConnection: boolean
  setupRequired: boolean
  isAuthenticated: boolean
  isSecureConnection: boolean
  username: string | null
}

type ServerSettings = {
  sessionLifetimeDays: number
  maxUploadBytes: number
  maxBatchUploadBytes: number
  minimumTextSimilarity: number
  maximumTextSimilarityDrop: number
  maximumSemanticCandidates: number
  ipAllowList: string[]
  ipDenyList: string[]
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
  modelInstallAvailable: boolean
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
  installState: string
  installPhase: string
  installProgressPercent: number
  installError: string | null
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
type GalleryLayout = 'photos' | 'structured'
type MediaKind = Asset['mediaKind']
type PreviewStage = 'color' | 'low' | 'medium' | 'native'
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
const ALL_MEDIA_KINDS: MediaKind[] = ['image', 'animation', 'video']
const MOBILE_PREVIEW_BATCH_SIZE = 4
const MOBILE_PREVIEW_BATCH_INTERVAL_MS = 80
const MOBILE_PREVIEW_LOAD_RADIUS_PX = 3000
const MOBILE_PREVIEW_RECENTER_DISTANCE_PX = 1500
const MOBILE_PREVIEW_RECENTER_DELAY_MS = 80
const MOBILE_PREVIEW_EVICTION_DELAY_MS = 5000
const mobilePreviewQueue = new Map<string, { ticket: number; apply: () => void }>()
const mobilePreviewTickets = new Map<string, number>()
let nextMobilePreviewTicket = 0
let mobilePreviewBatchTimer: number | undefined

function scheduleMobilePreviewBatch() {
  if (mobilePreviewBatchTimer !== undefined) return

  mobilePreviewBatchTimer = window.setTimeout(() => {
    mobilePreviewBatchTimer = undefined
    const batch = Array.from(mobilePreviewQueue.entries()).slice(0, MOBILE_PREVIEW_BATCH_SIZE)
    batch.forEach(([assetId]) => mobilePreviewQueue.delete(assetId))

    window.requestAnimationFrame(() => {
      batch.forEach(([assetId, update]) => {
        if (mobilePreviewTickets.get(assetId) !== update.ticket) return
        mobilePreviewTickets.delete(assetId)
        update.apply()
      })
    })

    if (mobilePreviewQueue.size > 0) scheduleMobilePreviewBatch()
  }, MOBILE_PREVIEW_BATCH_INTERVAL_MS)
}

function queuePreviewUpdate(assetId: string, isMobile: boolean, apply: () => void) {
  if (!isMobile) {
    apply()
    return
  }

  const ticket = ++nextMobilePreviewTicket
  mobilePreviewTickets.set(assetId, ticket)
  mobilePreviewQueue.set(assetId, { ticket, apply })
  scheduleMobilePreviewBatch()
}

function cancelQueuedPreviewUpdate(assetId: string) {
  mobilePreviewQueue.delete(assetId)
  mobilePreviewTickets.delete(assetId)
}

type MobilePreviewWindowSubscriber = {
  element: HTMLElement
  insideLoadWindow: boolean | null
  update: (insideLoadWindow: boolean) => void
}

const mobilePreviewWindowSubscribers = new Set<MobilePreviewWindowSubscriber>()
let mobilePreviewWindowAnchor: number | null = null
let mobilePreviewWindowTimer: number | undefined
let forceMobilePreviewWindowRefresh = false
let mobilePreviewWindowListening = false

function currentViewportCenter() {
  return window.scrollY + window.innerHeight / 2
}

function updateMobilePreviewSubscriber(
  subscriber: MobilePreviewWindowSubscriber,
  anchor: number,
) {
  const bounds = subscriber.element.getBoundingClientRect()
  const documentTop = bounds.top + window.scrollY
  const documentBottom = bounds.bottom + window.scrollY
  const insideLoadWindow =
    documentBottom >= anchor - MOBILE_PREVIEW_LOAD_RADIUS_PX &&
    documentTop <= anchor + MOBILE_PREVIEW_LOAD_RADIUS_PX
  if (subscriber.insideLoadWindow === insideLoadWindow) return

  subscriber.insideLoadWindow = insideLoadWindow
  subscriber.update(insideLoadWindow)
}

function refreshMobilePreviewWindow() {
  mobilePreviewWindowTimer = undefined
  const nextAnchor = currentViewportCenter()
  if (
    !forceMobilePreviewWindowRefresh &&
    mobilePreviewWindowAnchor !== null &&
    Math.abs(nextAnchor - mobilePreviewWindowAnchor) < MOBILE_PREVIEW_RECENTER_DISTANCE_PX
  ) {
    return
  }

  forceMobilePreviewWindowRefresh = false
  mobilePreviewWindowAnchor = nextAnchor
  mobilePreviewWindowSubscribers.forEach((subscriber) =>
    updateMobilePreviewSubscriber(subscriber, nextAnchor))
}

function scheduleMobilePreviewWindowRefresh(force = false, immediate = false) {
  forceMobilePreviewWindowRefresh ||= force
  if (mobilePreviewWindowTimer !== undefined) {
    if (!immediate) return
    window.clearTimeout(mobilePreviewWindowTimer)
  }

  mobilePreviewWindowTimer = window.setTimeout(
    refreshMobilePreviewWindow,
    immediate ? 0 : MOBILE_PREVIEW_RECENTER_DELAY_MS,
  )
}

function handleMobilePreviewScroll() {
  if (
    mobilePreviewWindowAnchor === null ||
    Math.abs(currentViewportCenter() - mobilePreviewWindowAnchor) >=
      MOBILE_PREVIEW_RECENTER_DISTANCE_PX
  ) {
    scheduleMobilePreviewWindowRefresh()
  }
}

function handleMobilePreviewScrollEnd() {
  if (
    mobilePreviewWindowAnchor === null ||
    Math.abs(currentViewportCenter() - mobilePreviewWindowAnchor) >=
      MOBILE_PREVIEW_RECENTER_DISTANCE_PX
  ) {
    scheduleMobilePreviewWindowRefresh(false, true)
  }
}

function startMobilePreviewWindowListeners() {
  if (mobilePreviewWindowListening) return
  mobilePreviewWindowListening = true
  window.addEventListener('scroll', handleMobilePreviewScroll, { passive: true })
  window.addEventListener('scrollend', handleMobilePreviewScrollEnd)
  window.addEventListener('resize', handleMobilePreviewScrollEnd)
}

function stopMobilePreviewWindowListeners() {
  if (!mobilePreviewWindowListening || mobilePreviewWindowSubscribers.size > 0) return
  mobilePreviewWindowListening = false
  window.removeEventListener('scroll', handleMobilePreviewScroll)
  window.removeEventListener('scrollend', handleMobilePreviewScrollEnd)
  window.removeEventListener('resize', handleMobilePreviewScrollEnd)
  if (mobilePreviewWindowTimer !== undefined) {
    window.clearTimeout(mobilePreviewWindowTimer)
    mobilePreviewWindowTimer = undefined
  }
  mobilePreviewWindowAnchor = null
  forceMobilePreviewWindowRefresh = false
}

function subscribeToMobilePreviewWindow(
  element: HTMLElement,
  update: (insideLoadWindow: boolean) => void,
) {
  const subscriber = { element, insideLoadWindow: null, update }
  mobilePreviewWindowSubscribers.add(subscriber)
  startMobilePreviewWindowListeners()

  if (mobilePreviewWindowAnchor === null) {
    scheduleMobilePreviewWindowRefresh(true)
  } else {
    const anchor = mobilePreviewWindowAnchor
    window.requestAnimationFrame(() => {
      if (mobilePreviewWindowSubscribers.has(subscriber)) {
        updateMobilePreviewSubscriber(subscriber, anchor)
      }
    })
  }

  return () => {
    mobilePreviewWindowSubscribers.delete(subscriber)
    stopMobilePreviewWindowListeners()
  }
}

function parseIpRules(value: string) {
  return value
    .split(/\r?\n/)
    .map((rule) => rule.trim())
    .filter((rule, index, rules) =>
      rule.length > 0 &&
      rules.findIndex((candidate) => candidate.toLowerCase() === rule.toLowerCase()) === index)
}

function parseFolderScope(value: string) {
  if (!value) return { folderId: null, relativePath: null }
  const separatorIndex = value.indexOf('::')
  return {
    folderId: value.slice(0, separatorIndex),
    relativePath: value.slice(separatorIndex + 2) || null,
  }
}

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

function getInitialGalleryLayout(): GalleryLayout {
  try {
    const savedLayout = window.localStorage.getItem('gluj-drive-gallery-layout')
    if (savedLayout === 'photos' || savedLayout === 'structured') {
      return savedLayout
    }
  } catch {
    // The default layout still works when local storage is unavailable.
  }

  return 'photos'
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

function ProgressivePhoto({
  asset,
  layout,
  onOpen,
}: {
  asset: Asset
  layout: GalleryLayout
  onOpen: (asset: Asset) => void
}) {
  const [previewStage, setPreviewStage] = useState<PreviewStage>('color')
  const cardRef = useRef<HTMLElement>(null)
  const sourceWidth = asset.pixelWidth && asset.pixelWidth > 0 ? asset.pixelWidth : 0
  const sourceHeight = asset.pixelHeight && asset.pixelHeight > 0 ? asset.pixelHeight : 0
  const aspectRatio = sourceWidth > 0 && sourceHeight > 0
    ? Math.min(6, Math.max(0.25, sourceWidth / sourceHeight))
    : 4 / 3

  useEffect(() => {
    const card = cardRef.current
    if (!card) return

    const isMobile = window.matchMedia('(max-width: 700px)').matches
    let lowTimer: number | undefined
    let mediumTimer: number | undefined
    let unloadTimer: number | undefined

    const clearPromotionTimers = () => {
      if (lowTimer) window.clearTimeout(lowTimer)
      if (mediumTimer) window.clearTimeout(mediumTimer)
    }

    const clearUnloadTimer = () => {
      if (unloadTimer) window.clearTimeout(unloadTimer)
    }

    const updatePreviewWindow = (insideLoadWindow: boolean) => {
      clearPromotionTimers()
      cancelQueuedPreviewUpdate(asset.id)

      if (!insideLoadWindow) {
        if (isMobile) {
          clearUnloadTimer()
          unloadTimer = window.setTimeout(
            () => queuePreviewUpdate(asset.id, true, () => setPreviewStage('color')),
            MOBILE_PREVIEW_EVICTION_DELAY_MS,
          )
        } else {
          setPreviewStage('color')
        }
        return
      }

      clearUnloadTimer()
      lowTimer = window.setTimeout(
        () => queuePreviewUpdate(asset.id, isMobile, () => setPreviewStage('low')),
        isMobile ? 180 : 150,
      )
      mediumTimer = window.setTimeout(
        () => queuePreviewUpdate(asset.id, isMobile, () => setPreviewStage('medium')),
        isMobile ? 600 : 500,
      )
    }

    const unsubscribe = isMobile
      ? subscribeToMobilePreviewWindow(card, updatePreviewWindow)
      : undefined
    const observer = isMobile
      ? undefined
      : new IntersectionObserver(
        ([entry]) => updatePreviewWindow(entry.isIntersecting),
        { rootMargin: '900px 0px', threshold: 0.01 },
      )

    observer?.observe(card)

    return () => {
      clearPromotionTimers()
      clearUnloadTimer()
      cancelQueuedPreviewUpdate(asset.id)
      unsubscribe?.()
      observer?.disconnect()
    }
  }, [asset.id])

  const previewUrl = previewStage === 'medium'
    ? asset.previewUrl
    : previewStage === 'low'
      ? asset.lowPreviewUrl
      : null

  const handlePreviewError = () => {
    setPreviewStage(asset.mediaKind === 'video' ? 'native' : 'color')
  }

  const pixelDensity = Math.min(2, Math.max(1, window.devicePixelRatio || 1))
  const displayWidth = sourceWidth > 0 ? sourceWidth / pixelDensity : 720
  const displayHeight = sourceHeight > 0 ? sourceHeight / pixelDensity : 220
  const basisForHeight = (targetHeight: number) =>
    `${aspectRatio * Math.min(targetHeight, displayHeight)}px`

  return (
    <article
      className="photo-card"
      data-layout={layout}
      ref={cardRef}
      style={{
        '--average-color': asset.averageColor,
        '--asset-ratio': aspectRatio,
        '--asset-basis': basisForHeight(220),
        '--asset-mobile-basis': basisForHeight(145),
        '--asset-phone-basis': basisForHeight(112),
        '--asset-max-width': `${Math.max(56, displayWidth)}px`,
      } as CSSProperties}
    >
      <button className="photo-link" type="button" onClick={() => onOpen(asset)} aria-label={'View ' + asset.fileName}>
        {previewUrl && previewStage !== 'native' && (
          <img
            src={previewUrl}
            alt={asset.fileName}
            loading="lazy"
            decoding="async"
            onError={handlePreviewError}
            width={sourceWidth || undefined}
            height={sourceHeight || undefined}
          />
        )}
        {previewStage === 'native' && asset.mediaKind === 'video' && (
          <video
            src={asset.viewUrl}
            muted
            playsInline
            preload="metadata"
            aria-label={asset.fileName}
          />
        )}
        <span className="media-badges">
          <span>{asset.fileExtension}</span>
          {asset.mediaKind !== 'image' && (
            <span>{asset.mediaKind === 'animation' ? 'Animation' : 'Video'}</span>
          )}
        </span>
        {asset.matchConfidence !== null && asset.matchConfidence !== undefined && (
          <span className="match-confidence" title="Semantic similarity score, not a probability">
            {Math.round(asset.matchConfidence * 100)}% similarity
          </span>
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

function PhotoGallery({
  assets,
  layout,
  onOpen,
  className = '',
}: {
  assets: Asset[]
  layout: GalleryLayout
  onOpen: (asset: Asset) => void
  className?: string
}) {
  return (
    <div className={`photo-grid${layout === 'photos' ? ' photo-grid-justified' : ''}${className ? ` ${className}` : ''}`}>
      {assets.map((asset) => (
        <ProgressivePhoto asset={asset} layout={layout} onOpen={onOpen} key={asset.id} />
      ))}
    </div>
  )
}

function AlbumSection({
  node,
  collapsedKeys,
  isHostConnection,
  galleryLayout,
  onToggle,
  onOpen,
  onEmpty,
}: {
  node: AlbumNode
  collapsedKeys: string[]
  isHostConnection: boolean
  galleryLayout: GalleryLayout
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
          <span>{assetCount} {assetCount === 1 ? 'item' : 'items'}</span>
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
              <PhotoGallery assets={datedAssets} layout={galleryLayout} onOpen={onOpen} />
            </section>
          ))}
          {node.children.map((child) => (
            <AlbumSection
              node={child}
              collapsedKeys={collapsedKeys}
              isHostConnection={isHostConnection}
              galleryLayout={galleryLayout}
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
  const lastPinchDistance = useRef<number | null>(null)
  const gestureStart = useRef<{ x: number; y: number; startedAt: number; pointerType: string } | null>(null)
  const gestureUsedPinch = useRef(false)
  const suppressBackdropClick = useRef(false)
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
    event.currentTarget.setPointerCapture(event.pointerId)
    if (pointers.current.size === 0) {
      gestureStart.current = {
        x: event.clientX,
        y: event.clientY,
        startedAt: performance.now(),
        pointerType: event.pointerType,
      }
      gestureUsedPinch.current = false
    }
    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
  }

  const handlePointerMove = (event: ReactPointerEvent<HTMLDivElement>) => {
    const previous = pointers.current.get(event.pointerId)
    if (!previous) return

    pointers.current.set(event.pointerId, { x: event.clientX, y: event.clientY })
    const activePointers = [...pointers.current.values()]

    if (activePointers.length === 2) {
      gestureUsedPinch.current = true
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
    const wasOnlyPointer = pointers.current.size === 1
    pointers.current.delete(event.pointerId)
    lastPinchDistance.current = null

    const start = gestureStart.current
    if (
      wasOnlyPointer &&
      start &&
      event.type === 'pointerup' &&
      start.pointerType !== 'mouse' &&
      !gestureUsedPinch.current &&
      scaleRef.current === 1 &&
      performance.now() - start.startedAt < 700
    ) {
      const horizontalTravel = event.clientX - start.x
      const verticalTravel = event.clientY - start.y
      if (Math.abs(horizontalTravel) >= 64 && Math.abs(horizontalTravel) > Math.abs(verticalTravel) * 1.25) {
        suppressBackdropClick.current = true
        moveTo(horizontalTravel > 0 ? currentIndex - 1 : currentIndex + 1)
      }
    }

    if (pointers.current.size === 0) {
      gestureStart.current = null
      gestureUsedPinch.current = false
    }
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
          {asset.matchConfidence !== null && asset.matchConfidence !== undefined && (
            <span title="Semantic similarity score, not a probability">
              {Math.round(asset.matchConfidence * 100)}% semantic similarity
            </span>
          )}
          <small title={formatAssetLocation(asset)}>{formatAssetLocation(asset)}</small>
        </div>
        <div className="viewer-controls">
          {asset.mediaKind !== 'video' && (
            <>
              <button type="button" onClick={() => changeScale(scaleRef.current / 1.25)} aria-label="Zoom out">−</button>
              <button type="button" onClick={resetView}>{Math.round(scale * 100)}%</button>
              <button type="button" onClick={() => changeScale(scaleRef.current * 1.25)} aria-label="Zoom in">+</button>
            </>
          )}
          <a href={asset.downloadUrl}>Download</a>
          <button type="button" onClick={() => void onFindSimilar(asset)} disabled={!canFindSimilar}>Find similar</button>
          <button className="viewer-delete" type="button" onClick={() => setConfirmDelete(true)}>Delete</button>
          <button className="viewer-close" type="button" onClick={onClose} aria-label="Close viewer">Close</button>
        </div>
      </div>

      <button
        className="viewer-nav viewer-previous"
        type="button"
        disabled={currentIndex <= 0}
        onClick={() => moveTo(currentIndex - 1)}
        aria-label="Previous media item"
      >‹</button>
      <div
        className={`viewer-stage${asset.mediaKind === 'video' ? ' viewer-stage-video' : ''}`}
        onClick={(event) => {
          if (suppressBackdropClick.current) {
            suppressBackdropClick.current = false
            return
          }
          if (event.target === event.currentTarget) {
            onClose()
          }
        }}
        onWheel={asset.mediaKind === 'video' ? undefined : handleWheel}
        onPointerDown={asset.mediaKind === 'video' ? undefined : handlePointerDown}
        onPointerMove={asset.mediaKind === 'video' ? undefined : handlePointerMove}
        onPointerUp={asset.mediaKind === 'video' ? undefined : handlePointerEnd}
        onPointerCancel={asset.mediaKind === 'video' ? undefined : handlePointerEnd}
        onDoubleClick={asset.mediaKind === 'video' ? undefined : () => changeScale(scaleRef.current > 1 ? 1 : 2)}
      >
        {asset.mediaKind === 'video' ? (
          <video
            key={asset.id}
            className="viewer-video"
            src={asset.viewUrl}
            controls
            autoPlay
            playsInline
            preload="metadata"
          />
        ) : (
          <img
            src={asset.viewUrl}
            alt={asset.fileName}
            draggable="false"
            style={{ transform: `translate3d(${position.x}px, ${position.y}px, 0) scale(${scale})` }}
          />
        )}
      </div>
      <button
        className="viewer-nav viewer-next"
        type="button"
        disabled={currentIndex >= assets.length - 1}
        onClick={() => moveTo(currentIndex + 1)}
        aria-label="Next media item"
      >›</button>

      {confirmDelete && (
        <div className="confirm-card" role="alertdialog" aria-modal="true" aria-labelledby="delete-picture-title">
          <p className="eyebrow">Move to trash</p>
          <h2 id="delete-picture-title">Delete this media item?</h2>
          <p><strong>{asset.fileName}</strong> will be moved to the folder’s <code>.gluj-trash</code> directory.</p>
          <div className="confirm-actions">
            <button type="button" onClick={() => setConfirmDelete(false)} disabled={isDeleting}>Keep item</button>
            <button className="destructive-action" type="button" onClick={() => void deleteAsset()} disabled={isDeleting}>
              {isDeleting ? 'Deleting…' : 'Move to trash'}
            </button>
          </div>
        </div>
      )}
    </div>
  )
}

function LibraryApp({
  authStatus,
  onAuthStatusChange,
}: {
  authStatus: AuthStatus
  onAuthStatusChange: (status: AuthStatus) => void
}) {
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
  const [showSettings, setShowSettings] = useState(false)
  const [serverSettings, setServerSettings] = useState<ServerSettings | null>(null)
  const [ipAllowListText, setIpAllowListText] = useState('')
  const [ipDenyListText, setIpDenyListText] = useState('')
  const [isSavingSettings, setIsSavingSettings] = useState(false)
  const [settingsNotice, setSettingsNotice] = useState<string | null>(null)
  const [accountName, setAccountName] = useState(authStatus.username ?? 'root')
  const [newAccountPassword, setNewAccountPassword] = useState('')
  const [confirmAccountPassword, setConfirmAccountPassword] = useState('')
  const [semanticStatus, setSemanticStatus] = useState<SemanticStatus | null>(null)
  const [semanticDevices, setSemanticDevices] = useState<SemanticDevice[]>([])
  const [isAiActionPending, setIsAiActionPending] = useState(false)
  const [newFolderPath, setNewFolderPath] = useState('')
  const [newFolderName, setNewFolderName] = useState('')
  const [makeNewFolderDefault, setMakeNewFolderDefault] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [theme, setTheme] = useState<Theme>(getInitialTheme)
  const [libraryView, setLibraryView] = useState<LibraryView>('timeline')
  const [galleryLayout, setGalleryLayout] = useState<GalleryLayout>(getInitialGalleryLayout)
  const [showLibraryFilters, setShowLibraryFilters] = useState(false)
  const [searchQuery, setSearchQuery] = useState('')
  const [searchScope, setSearchScope] = useState('')
  const [searchMediaKinds, setSearchMediaKinds] = useState<MediaKind[]>(ALL_MEDIA_KINDS)
  const [searchView, setSearchView] = useState<SearchView | null>(null)
  const [isSearching, setIsSearching] = useState(false)
  const [visibleCount, setVisibleCount] = useState(LOAD_BATCH_SIZE)
  const [collapsedFolderIds, setCollapsedFolderIds] = useState<string[]>([])
  const [viewerAsset, setViewerAsset] = useState<Asset | null>(null)
  const [folderPendingEmpty, setFolderPendingEmpty] = useState<SourceFolder | null>(null)
  const [isEmptyingFolder, setIsEmptyingFolder] = useState(false)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const loadMoreRef = useRef<HTMLDivElement>(null)
  const libraryControlRef = useRef<HTMLElement>(null)
  const searchRequestRef = useRef(0)

  useLayoutEffect(() => {
    document.documentElement.dataset.theme = theme

    try {
      window.localStorage.setItem('gluj-drive-theme', theme)
    } catch {
      // The selected theme still applies for the current session.
    }
  }, [theme])

  useEffect(() => {
    try {
      window.localStorage.setItem('gluj-drive-gallery-layout', galleryLayout)
    } catch {
      // The selected layout still applies for the current session.
    }
  }, [galleryLayout])

  useEffect(() => {
    if (!showLibraryFilters) return

    const closeWhenOutside = (event: PointerEvent) => {
      if (event.target instanceof Node && !libraryControlRef.current?.contains(event.target)) {
        setShowLibraryFilters(false)
      }
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setShowLibraryFilters(false)
    }

    document.addEventListener('pointerdown', closeWhenOutside)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('pointerdown', closeWhenOutside)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [showLibraryFilters])

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
          nextFolder?.subfolders.some((subfolder) =>
            subfolder.relativePath === currentPath && !subfolder.isExcluded)
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
        setError(caughtError instanceof Error ? caughtError.message : 'Could not load semantic search status.')
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

  useEffect(() => {
    if (!showSettings || !capabilities?.isHostConnection) return
    const controller = new AbortController()
    void fetch('/api/settings', { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(await getErrorMessage(response))
        const loadedSettings = (await response.json()) as ServerSettings
        setServerSettings(loadedSettings)
        setIpAllowListText(loadedSettings.ipAllowList.join('\n'))
        setIpDenyListText(loadedSettings.ipDenyList.join('\n'))
      })
      .catch((caughtError: unknown) => {
        if (!(caughtError instanceof DOMException && caughtError.name === 'AbortError')) {
          setError(caughtError instanceof Error ? caughtError.message : 'Could not load server settings.')
        }
      })
    return () => controller.abort()
  }, [capabilities?.isHostConnection, showSettings])

  const saveServerSettings = async (event: FormEvent) => {
    event.preventDefault()
    if (!serverSettings) return
    setIsSavingSettings(true)
    setSettingsNotice(null)
    try {
      const requestedSettings = {
        ...serverSettings,
        ipAllowList: parseIpRules(ipAllowListText),
        ipDenyList: parseIpRules(ipDenyListText),
      }
      const response = await fetch('/api/settings', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(requestedSettings),
      })
      if (!response.ok) throw new Error(await getErrorMessage(response))
      const result = (await response.json()) as { settings: ServerSettings; restartRequired: boolean }
      setServerSettings(result.settings)
      setIpAllowListText(result.settings.ipAllowList.join('\n'))
      setIpDenyListText(result.settings.ipDenyList.join('\n'))
      setSettingsNotice(result.restartRequired
        ? 'Saved. Restart the server before using the increased upload limit.'
        : 'Server settings saved.')
    } catch (caughtError: unknown) {
      setError(caughtError instanceof Error ? caughtError.message : 'Could not save server settings.')
    } finally {
      setIsSavingSettings(false)
    }
  }

  const updateRootAccount = async (event: FormEvent) => {
    event.preventDefault()
    if (newAccountPassword !== confirmAccountPassword) {
      setError('The new passwords do not match.')
      return
    }
    setIsSavingSettings(true)
    setSettingsNotice(null)
    try {
      const response = await fetch('/api/auth/account', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username: accountName, password: newAccountPassword }),
      })
      if (!response.ok) throw new Error(await getErrorMessage(response))
      const status = (await response.json()) as AuthStatus
      onAuthStatusChange(status)
      setNewAccountPassword('')
      setConfirmAccountPassword('')
      setSettingsNotice('Owner account updated. Existing remote sessions were signed out.')
    } catch (caughtError: unknown) {
      setError(caughtError instanceof Error ? caughtError.message : 'Could not update the owner account.')
    } finally {
      setIsSavingSettings(false)
    }
  }

  const logout = async () => {
    await fetch('/api/auth/logout', { method: 'POST' })
    onAuthStatusChange({ ...authStatus, isAuthenticated: false, username: null })
  }

  const runAiAction = async (path: string, init?: RequestInit) => {
    setIsAiActionPending(true)
    setError(null)
    try {
      const response = await fetch(path, init)
      if (!response.ok) throw new Error(await getErrorMessage(response))
      await loadAiStatus()
    } catch (caughtError: unknown) {
      setError(caughtError instanceof Error ? caughtError.message : 'The semantic search operation failed.')
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
      setError('Select an available upload folder and at least one media file.')
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
          : 'The media files could not be uploaded.',
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

  const setSubfolderExcluded = async (
    folder: SourceFolder,
    subfolder: SourceSubfolder,
  ) => {
    const excluded = !subfolder.isDirectlyExcluded
    setIsUpdatingFolders(true)
    setError(null)

    setFolders((current) => current.map((candidate) => candidate.id !== folder.id
      ? candidate
      : {
          ...candidate,
          subfolders: candidate.subfolders.map((item) => {
            const isTargetOrDescendant = item.relativePath === subfolder.relativePath ||
              item.relativePath.startsWith(subfolder.relativePath + '/')
            return isTargetOrDescendant
              ? {
                  ...item,
                  isExcluded: excluded,
                  isDirectlyExcluded: item.relativePath === subfolder.relativePath && excluded,
                }
              : item
          }),
        }))

    try {
      const response = await fetch(`/api/folders/${folder.id}/subfolders/exclusion`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ relativePath: subfolder.relativePath, excluded }),
      })

      if (!response.ok) throw new Error(await getErrorMessage(response))
      await loadLibrary()
    } catch (caughtError: unknown) {
      setError(caughtError instanceof Error
        ? caughtError.message
        : 'The subfolder scan setting could not be changed.')
      await loadLibrary()
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
    scope: string,
    mediaKinds: MediaKind[],
  ) => {
    setIsSearching(true)
    try {
      const parsedScope = parseFolderScope(scope)
      const response = await fetch('/api/search', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          query,
          pageSize: 100,
          cursor,
          folderId: parsedScope.folderId,
          relativePath: parsedScope.relativePath,
          mediaKinds: mediaKinds.length === ALL_MEDIA_KINDS.length ? null : mediaKinds,
        }),
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
        label: !query
          ? 'Filtered library'
          : result.semanticParticipated
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
    const hasActiveFilters = Boolean(searchScope) || searchMediaKinds.length !== ALL_MEDIA_KINDS.length
    const requestId = ++searchRequestRef.current

    if (!query && !hasActiveFilters) {
      setSearchView((current) => current?.kind === 'query' ? null : current)
      setIsSearching(false)
      return
    }

    setSearchView(null)
    setIsSearching(true)
    const timer = window.setTimeout(() => {
      void loadSearchPage(query, null, false, requestId, searchScope, searchMediaKinds)
    }, 300)
    return () => window.clearTimeout(timer)
  }, [loadSearchPage, searchMediaKinds, searchQuery, searchScope])

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
      setSearchScope('')
      setSearchMediaKinds(ALL_MEDIA_KINDS)
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
    const hasActiveFilters = Boolean(searchScope) || searchMediaKinds.length !== ALL_MEDIA_KINDS.length
    return searchQuery.trim() || hasActiveFilters ? [] : assets
  }, [assets, searchMediaKinds.length, searchQuery, searchScope, searchView])
  const activeFilterCount =
    (searchScope ? 1 : 0) +
    (searchMediaKinds.length === ALL_MEDIA_KINDS.length ? 0 : 1)

  const orderedAssets = useMemo(() => {
    if (searchView) {
      // The server has the complete result set and preserves exact lexical
      // matches before similarity-ranked results across cursor pages.
      return [...filteredAssets]
    }

    return [...filteredAssets].sort((left, right) =>
      new Date(right.modifiedAtUtc).getTime() - new Date(left.modifiedAtUtc).getTime())
  }, [filteredAssets, searchView])
  const visibleAssets = orderedAssets.slice(0, visibleCount)
  const hasMoreAssets = visibleCount < orderedAssets.length || Boolean(searchView?.nextCursor)

  useEffect(() => {
    setVisibleCount(LOAD_BATCH_SIZE)
  }, [libraryView, searchMediaKinds, searchQuery, searchScope, searchView?.label])

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
              searchScope,
              searchMediaKinds,
            )
          }
        }
      },
      { rootMargin: '500px 0px' },
    )

    observer.observe(target)
    return () => observer.disconnect()
  }, [hasMoreAssets, isSearching, loadSearchPage, orderedAssets.length, searchMediaKinds, searchQuery, searchScope, searchView, visibleCount])

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

  const toggleSearchMediaKind = (kind: MediaKind) => {
    setSearchView((current) => current?.kind === 'similar' ? null : current)
    setSearchMediaKinds((current) =>
      current.length === 1 && current[0] === kind ? ALL_MEDIA_KINDS : [kind])
  }

  const resetSearch = () => {
    setSearchQuery('')
    setSearchScope('')
    setSearchMediaKinds(ALL_MEDIA_KINDS)
    setSearchView(null)
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
              onClick={() => setShowSettings((current) => !current)}
              aria-expanded={showSettings}
            >
              Settings
            </button>
          )}

          {capabilities?.isHostConnection && (
            <button
              className="secondary-button"
              type="button"
              onClick={() => setShowAiManager((current) => !current)}
              aria-expanded={showAiManager}
            >
              Semantic search
            </button>
          )}

          {!authStatus.isHostConnection && authStatus.isAuthenticated && (
            <button className="secondary-button" type="button" onClick={() => void logout()}>
              Sign out
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

      {showSettings && capabilities?.isHostConnection && (
        <section className="settings-manager" aria-labelledby="settings-title">
          <div className="settings-heading">
            <div>
              <p className="eyebrow">Available on this PC</p>
              <h2 id="settings-title">Server settings</h2>
              <p>Manage remote access, uploads, and search behavior without editing configuration files.</p>
            </div>
            {settingsNotice && <span className="settings-notice">{settingsNotice}</span>}
          </div>

          {!serverSettings ? (
            <div className="ai-loading"><span className="loading-dot" /> Loading settings...</div>
          ) : (
            <div className="settings-grid">
              <form className="settings-card" onSubmit={(event) => void saveServerSettings(event)}>
                <h3>Server preferences</h3>
                <p>Changes are stored in the application catalog and survive upgrades.</p>
                <div className="settings-fields">
                  <label className="full-width-field">
                    <span>IP allow list</span>
                    <textarea
                      rows={4}
                      value={ipAllowListText}
                      placeholder={'Leave empty to allow all addresses\nor enter one IP/CIDR per line'}
                      spellCheck={false}
                      onChange={(event) => setIpAllowListText(event.target.value)}
                    />
                    <small>When populated, only matching remote addresses can connect. Loopback always remains available.</small>
                  </label>
                  <label className="full-width-field">
                    <span>IP deny list</span>
                    <textarea
                      rows={4}
                      value={ipDenyListText}
                      placeholder={'One IP or CIDR per line\nExample: 192.168.1.50'}
                      spellCheck={false}
                      onChange={(event) => setIpDenyListText(event.target.value)}
                    />
                    <small>Deny rules take priority over the allow list. Both IPv4 and IPv6 CIDR ranges are supported.</small>
                  </label>
                  <label>
                    <span>Keep remote devices signed in</span>
                    <div className="number-with-unit">
                      <input
                        type="number"
                        min="1"
                        max="365"
                        value={serverSettings.sessionLifetimeDays}
                        onChange={(event) => setServerSettings({ ...serverSettings, sessionLifetimeDays: Number(event.target.value) })}
                      />
                      <span>days</span>
                    </div>
                  </label>
                  <label>
                    <span>Maximum file upload</span>
                    <div className="number-with-unit">
                      <input
                        type="number"
                        min="1"
                        value={Math.round(serverSettings.maxUploadBytes / 1024 / 1024)}
                        onChange={(event) => setServerSettings({ ...serverSettings, maxUploadBytes: Number(event.target.value) * 1024 * 1024 })}
                      />
                      <span>MB</span>
                    </div>
                  </label>
                  <label>
                    <span>Maximum upload batch</span>
                    <div className="number-with-unit">
                      <input
                        type="number"
                        min="1"
                        value={Math.round(serverSettings.maxBatchUploadBytes / 1024 / 1024)}
                        onChange={(event) => setServerSettings({ ...serverSettings, maxBatchUploadBytes: Number(event.target.value) * 1024 * 1024 })}
                      />
                      <span>MB</span>
                    </div>
                  </label>
                  <label>
                    <span>Minimum semantic similarity</span>
                    <input
                      type="number"
                      min="-1"
                      max="1"
                      step="0.01"
                      value={serverSettings.minimumTextSimilarity}
                      onChange={(event) => setServerSettings({ ...serverSettings, minimumTextSimilarity: Number(event.target.value) })}
                    />
                  </label>
                  <label>
                    <span>Allowed drop from best match</span>
                    <input
                      type="number"
                      min="0"
                      max="2"
                      step="0.01"
                      value={serverSettings.maximumTextSimilarityDrop}
                      onChange={(event) => setServerSettings({ ...serverSettings, maximumTextSimilarityDrop: Number(event.target.value) })}
                    />
                  </label>
                  <label>
                    <span>Maximum semantic matches</span>
                    <input
                      type="number"
                      min="1"
                      max="1000"
                      value={serverSettings.maximumSemanticCandidates}
                      onChange={(event) => setServerSettings({ ...serverSettings, maximumSemanticCandidates: Number(event.target.value) })}
                    />
                  </label>
                </div>
                <button className="secondary-button" type="submit" disabled={isSavingSettings}>
                  {isSavingSettings ? 'Saving...' : 'Save server settings'}
                </button>
              </form>

              <form className="settings-card" onSubmit={(event) => void updateRootAccount(event)}>
                <h3>Owner account</h3>
                <p>Changing this account invalidates every existing remote login.</p>
                <div className="settings-fields single-column">
                  <label>
                    <span>Account name</span>
                    <input value={accountName} minLength={3} maxLength={64} onChange={(event) => setAccountName(event.target.value)} required />
                  </label>
                  <label>
                    <span>New password</span>
                    <input type="password" minLength={12} maxLength={256} autoComplete="new-password" value={newAccountPassword} onChange={(event) => setNewAccountPassword(event.target.value)} required />
                  </label>
                  <label>
                    <span>Confirm new password</span>
                    <input type="password" minLength={12} maxLength={256} autoComplete="new-password" value={confirmAccountPassword} onChange={(event) => setConfirmAccountPassword(event.target.value)} required />
                  </label>
                </div>
                <button className="secondary-button" type="submit" disabled={isSavingSettings}>
                  {isSavingSettings ? 'Updating...' : 'Update owner account'}
                </button>
              </form>
            </div>
          )}
        </section>
      )}

      {showAiManager && capabilities?.isHostConnection && (
        <section className="ai-manager" aria-labelledby="ai-manager-title">
          <div className="ai-manager-heading">
            <div>
              <p className="eyebrow">Local and optional</p>
              <h2 id="ai-manager-title">Semantic search</h2>
              <p>Analyze first-frame pixels only when you ask. Media and searches stay on this computer.</p>
            </div>
            <span className="ai-state" data-state={semanticStatus?.job.state ?? 'idle'}>
              {semanticStatus?.job.state ?? 'Loading'}
            </span>
          </div>

          {!semanticStatus ? (
            <div className="ai-loading"><span className="loading-dot" /> Loading search status...</div>
          ) : (
            <>
              <div className="ai-stats">
                <div><strong>{semanticStatus.indexed}</strong><span>Analyzed</span></div>
                <div><strong>{semanticStatus.remaining}</strong><span>Remaining</span></div>
                <div><strong>{semanticStatus.stale}</strong><span>Outdated</span></div>
                <div><strong>{semanticStatus.failed}</strong><span>Failed</span></div>
              </div>

              <div className="ai-progress" aria-label={`${semanticStatus.coveragePercent.toFixed(1)}% analyzed`}>
                <span style={{ width: `${Math.min(100, semanticStatus.coveragePercent)}%` }} />
              </div>

              {semanticStatus.installState === 'installing' && (
                <div className="ai-active-progress">
                  <div><span>{semanticStatus.installPhase}</span><strong>{Math.round(semanticStatus.installProgressPercent)}%</strong></div>
                  <div
                    className="ai-progress"
                    role="progressbar"
                    aria-valuemin={0}
                    aria-valuemax={100}
                    aria-valuenow={Math.round(semanticStatus.installProgressPercent)}
                  >
                    <span style={{ width: `${Math.min(100, semanticStatus.installProgressPercent)}%` }} />
                  </div>
                </div>
              )}

              {semanticStatus.job.state === 'running' && (
                <div className="ai-active-progress" aria-live="polite">
                  <div>
                    <span>{semanticStatus.job.cancellationPending ? 'Finishing current item' : 'Analyzing library'}</span>
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
                    aria-valuetext={`${semanticStatus.job.processed} of ${semanticStatus.job.total} media items`}
                  >
                    <span style={{ width: `${semanticStatus.job.total > 0 ? Math.min(100, semanticStatus.job.processed * 100 / semanticStatus.job.total) : 0}%` }} />
                  </div>
                </div>
              )}

              <div className="ai-controls">
                <label className="folder-select">
                  <span>Processing device</span>
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

                {(!semanticStatus.modelInstalled || !semanticStatus.runtimeAvailable) && (
                  <button
                    className="secondary-button"
                    type="button"
                    disabled={isAiActionPending || !semanticStatus.modelInstallAvailable || semanticStatus.installState === 'installing'}
                    onClick={() => void runAiAction('/api/ai/install', { method: 'POST' })}
                    title={semanticStatus.modelInstallAvailable ? undefined : 'This build does not include a semantic-search package and has no release download configured.'}
                  >
                    {semanticStatus.installState === 'installing'
                      ? 'Installing...'
                      : semanticStatus.modelInstalled
                        ? 'Repair semantic search'
                        : 'Install semantic search'}
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
                      Analyze everything again
                    </button>
                  </>
                )}
              </div>

              <div className="ai-detail">
                <span>{semanticStatus.modelInstalled ? `${semanticStatus.modelId} ${semanticStatus.modelVersion ?? ''}` : 'Search model not installed'}</span>
                <span>{semanticStatus.activeDevice ?? (semanticStatus.runtimeAvailable ? 'Search engine ready' : 'Search engine not installed')}</span>
                {semanticStatus.job.currentFile && <code title={semanticStatus.job.currentFile}>{semanticStatus.job.currentFile}</code>}
                {semanticStatus.job.state === 'running' && (
                  <span>
                    {semanticStatus.job.processed} of {semanticStatus.job.total} · {semanticStatus.job.imagesPerSecond.toFixed(1)} items/s
                    {semanticStatus.job.estimatedSecondsRemaining !== null ? ` · about ${semanticStatus.job.estimatedSecondsRemaining}s left` : ''}
                  </span>
                )}
                {(semanticStatus.fallbackReason || semanticStatus.installError || semanticStatus.job.error) && (
                  <span className="ai-warning">{semanticStatus.fallbackReason ?? semanticStatus.installError ?? semanticStatus.job.error}</span>
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
              <h2 id="folder-manager-title">Library folders</h2>
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
                          <div
                            className="source-subfolder"
                            data-excluded={subfolder.isExcluded}
                            key={subfolder.relativePath}
                            style={{ '--subfolder-depth': depth } as CSSProperties}
                            title={`${folder.name} / ${subfolder.relativePath}`}
                          >
                            <span className="source-subfolder-name">
                              <i aria-hidden="true">↳</i> {subfolder.name}
                            </span>
                            <button
                              type="button"
                              className="subfolder-scan-toggle"
                              aria-pressed={!subfolder.isExcluded}
                              disabled={isUpdatingFolders || (subfolder.isExcluded && !subfolder.isDirectlyExcluded)}
                              title={subfolder.isExcluded && !subfolder.isDirectlyExcluded
                                ? 'Restore the excluded parent folder first'
                                : subfolder.isDirectlyExcluded
                                  ? 'Include this subfolder in the library again'
                                  : 'Exclude this subfolder and everything below it'}
                              onClick={() => void setSubfolderExcluded(folder, subfolder)}
                            >
                              {subfolder.isDirectlyExcluded ? 'Restore' : 'Exclude'}
                            </button>
                          </div>
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
          <p>Media discovered in your library folders.</p>
        </div>
        <span className="asset-count">
          {assets.length} {assets.length === 1 ? 'media item' : 'media items'}
        </span>
      </section>

      <nav className="library-control-bar" ref={libraryControlRef} aria-label="Library display controls">
      <span className="library-control-title">Library controls</span>
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
        <div className="gallery-layout-toggle" role="group" aria-label="Gallery layout">
          <button
            type="button"
            className={galleryLayout === 'photos' ? 'active' : ''}
            aria-pressed={galleryLayout === 'photos'}
            title="Fill rows using each media item's aspect ratio"
            onClick={() => setGalleryLayout('photos')}
          >
            Photos
          </button>
          <button
            type="button"
            className={galleryLayout === 'structured' ? 'active' : ''}
            aria-pressed={galleryLayout === 'structured'}
            title="Use equal cards with file details and actions"
            onClick={() => setGalleryLayout('structured')}
          >
            Cards
          </button>
        </div>
        <button
          className="secondary-button view-toggle"
          type="button"
          onClick={() => setLibraryView((current) => current === 'timeline' ? 'folders' : 'timeline')}
        >
          {libraryView === 'timeline' ? 'Albums' : 'Timeline'}
        </button>
        <button
          className="secondary-button mobile-filter-toggle"
          type="button"
          aria-expanded={showLibraryFilters}
          aria-controls="library-search-filters"
          onClick={() => setShowLibraryFilters((current) => !current)}
        >
          Filters
          {activeFilterCount > 0 && <span>{activeFilterCount}</span>}
        </button>
      </div>
      <div
        id="library-search-filters"
        className={`search-filters${showLibraryFilters ? ' mobile-open' : ''}`}
        aria-label="Search filters"
      >
        <label className="search-scope">
          <span>Search within</span>
          <select
            value={searchScope}
            onChange={(event) => {
              setSearchView((current) => current?.kind === 'similar' ? null : current)
              setSearchScope(event.target.value)
            }}
          >
            <option value="">Every folder</option>
            {folders.filter((folder) => folder.isAvailable).map((folder) => (
              <optgroup label={folder.name} key={folder.id}>
                <option value={`${folder.id}::`}>{folder.name} / root and subfolders</option>
                {folder.subfolders.filter((subfolder) => !subfolder.isExcluded).map((subfolder) => (
                  <option value={`${folder.id}::${subfolder.relativePath}`} key={subfolder.relativePath}>
                    {'\u00a0\u00a0'.repeat(subfolder.relativePath.split('/').length)}↳ {subfolder.name}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>
        </label>
        <div className="media-filter-group" role="group" aria-label="Media types">
          <span>Media type</span>
          <div>
            <button
              type="button"
              className={searchMediaKinds.length === ALL_MEDIA_KINDS.length ? 'active' : ''}
              aria-pressed={searchMediaKinds.length === ALL_MEDIA_KINDS.length}
              onClick={() => {
                setSearchView((current) => current?.kind === 'similar' ? null : current)
                setSearchMediaKinds(ALL_MEDIA_KINDS)
              }}
            >All</button>
            {([['image', 'Images'], ['animation', 'GIFs'], ['video', 'Videos']] as const).map(([kind, label]) => (
              <button
                type="button"
                className={searchMediaKinds.includes(kind) ? 'active' : ''}
                aria-pressed={searchMediaKinds.includes(kind)}
                onClick={() => toggleSearchMediaKind(kind)}
                key={kind}
              >{label}</button>
            ))}
          </div>
          </div>
          {(searchQuery || searchScope || searchMediaKinds.length !== ALL_MEDIA_KINDS.length) && (
            <button
              className="reset-filters"
              type="button"
              onClick={() => {
                resetSearch()
                setShowLibraryFilters(false)
              }}
            >
              Reset search
            </button>
          )}
      </div>
      </nav>

      <section className="upload-panel" aria-label="Upload media">
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
                {folder.subfolders.filter((subfolder) => !subfolder.isExcluded).map((subfolder) => (
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
          accept="image/jpeg,image/png,image/webp,image/gif,image/heic,image/heif,video/mp4,video/webm,video/ogg,video/quicktime,.heic,.heif,.m4v,.mov,.ogv"
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
            ? 'Uploading ' + uploadCount + (uploadCount === 1 ? ' file...' : ' files...')
            : 'Upload media'}
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
          Scanning your library folders...
        </div>
      ) : assets.length === 0 ? (
        <div className="empty-state">
          <strong>No media found</strong>
          <span>Upload media or register a folder that already contains some.</span>
        </div>
      ) : isSearching && filteredAssets.length === 0 ? (
        <div className="empty-state" aria-live="polite">
          <span className="loading-dot" />
          Searching your library...
        </div>
      ) : filteredAssets.length === 0 ? (
        <div className="empty-state">
          <strong>No matching media</strong>
          <span>Try a different filename or description.</span>
          <button className="secondary-button" type="button" onClick={resetSearch}>Clear search</button>
        </div>
      ) : (
        <>
        {searchView ? (
          <PhotoGallery
            assets={visibleAssets}
            layout={galleryLayout}
            onOpen={setViewerAsset}
            className="search-results-grid"
          />
        ) : libraryView === 'timeline' ? (
          <div className="timeline-groups">
            {groupAssetsByMonth(visibleAssets).map(([dateKey, datedAssets]) => (
              <section className="date-group" id={`date-${dateKey}`} key={dateKey}>
                <h3>{formatMonth(datedAssets[0].modifiedAtUtc)}</h3>
                <PhotoGallery assets={datedAssets} layout={galleryLayout} onOpen={setViewerAsset} />
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
                galleryLayout={galleryLayout}
                onToggle={toggleFolder}
                onOpen={setViewerAsset}
                onEmpty={setFolderPendingEmpty}
                key={tree.key}
              />
            ))}
          </div>
        )}
        {!searchView && libraryView === 'timeline' && dateRailEntries.length > 1 && (
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
            <><span className="loading-dot" /> Loading more media…</>
          ) : (
            <span>All {searchView?.total ?? filteredAssets.length} media items are displayed.</span>
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
            <p>Every supported media file in this folder and its subfolders will be moved into <code>.gluj-trash</code>. This affects the real files on the host computer.</p>
            <p className="warning-count">{assets.filter((asset) => asset.folderId === folderPendingEmpty.id).length} media items are currently scanned.</p>
            <div className="confirm-actions">
              <button type="button" onClick={() => setFolderPendingEmpty(null)} disabled={isEmptyingFolder}>Cancel</button>
              <button className="destructive-action" type="button" onClick={() => void emptyFolder(folderPendingEmpty)} disabled={isEmptyingFolder}>
                {isEmptyingFolder ? 'Moving media…' : 'Yes, empty folder'}
              </button>
            </div>
          </div>
        </div>
      )}
    </main>
  )
}

function App() {
  const [authStatus, setAuthStatus] = useState<AuthStatus | null>(null)
  const [username, setUsername] = useState('root')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [authError, setAuthError] = useState<string | null>(null)
  const [isSubmitting, setIsSubmitting] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    void fetch('/api/auth/status', { signal: controller.signal })
      .then(async (response) => {
        if (!response.ok) throw new Error(await getErrorMessage(response))
        const status = (await response.json()) as AuthStatus
        setAuthStatus(status)
        if (status.username) setUsername(status.username)
      })
      .catch((caughtError: unknown) => {
        if (!(caughtError instanceof DOMException && caughtError.name === 'AbortError')) {
          setAuthError(caughtError instanceof Error ? caughtError.message : 'Could not contact Gluj Drive.')
        }
      })
    return () => controller.abort()
  }, [])

  const submitCredentials = async (event: FormEvent) => {
    event.preventDefault()
    if (!authStatus) return
    if (authStatus.setupRequired && password !== confirmPassword) {
      setAuthError('The passwords do not match.')
      return
    }

    setIsSubmitting(true)
    setAuthError(null)
    try {
      const response = await fetch(authStatus.setupRequired ? '/api/auth/setup' : '/api/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password }),
      })
      if (!response.ok) throw new Error(await getErrorMessage(response))
      setAuthStatus((await response.json()) as AuthStatus)
      setPassword('')
      setConfirmPassword('')
    } catch (caughtError: unknown) {
      setAuthError(caughtError instanceof Error ? caughtError.message : 'Authentication failed.')
    } finally {
      setIsSubmitting(false)
    }
  }

  if (!authStatus) {
    return (
      <main className="auth-shell">
        <section className="auth-card">
          <p className="eyebrow">Personal media server</p>
          <h1>Gluj Drive</h1>
          {authError
            ? <div className="auth-error"><strong>Connection failed: </strong>{authError}</div>
            : <div className="auth-loading"><span className="loading-dot" /> Checking server access...</div>}
        </section>
      </main>
    )
  }

  if (authStatus.setupRequired && !authStatus.isHostConnection) {
    return (
      <main className="auth-shell">
        <section className="auth-card">
          <p className="eyebrow">Owner setup required</p>
          <h1>Gluj Drive</h1>
          <h2>Finish setup on the host PC</h2>
          <p>The media library remains locked until its owner opens this page directly on the Windows computer and creates the owner account.</p>
        </section>
      </main>
    )
  }

  if (authStatus.setupRequired || (!authStatus.isHostConnection && !authStatus.isAuthenticated)) {
    const isSetup = authStatus.setupRequired
    return (
      <main className="auth-shell">
        <form className="auth-card" onSubmit={(event) => void submitCredentials(event)}>
          <p className="eyebrow">{isSetup ? 'First launch' : 'Private media library'}</p>
          <h1>Gluj Drive</h1>
          <h2>{isSetup ? 'Create the owner account' : 'Sign in'}</h2>
          <p>{isSetup
            ? 'This single account protects every connection from another computer or phone. Local access on this PC can always bypass sign-in.'
            : 'Use the owner account created on the host computer.'}</p>
          <label>
            <span>Account name</span>
            <input value={username} minLength={3} maxLength={64} autoComplete="username" onChange={(event) => setUsername(event.target.value)} required autoFocus />
          </label>
          <label>
            <span>Password</span>
            <input type="password" minLength={12} maxLength={256} autoComplete={isSetup ? 'new-password' : 'current-password'} value={password} onChange={(event) => setPassword(event.target.value)} required />
          </label>
          {isSetup && (
            <label>
              <span>Confirm password</span>
              <input type="password" minLength={12} maxLength={256} autoComplete="new-password" value={confirmPassword} onChange={(event) => setConfirmPassword(event.target.value)} required />
            </label>
          )}
          {authError && <div className="auth-error" role="alert">{authError}</div>}
          {!isSetup && !authStatus.isSecureConnection && (
            <div className="auth-warning">This connection is not encrypted. Use HTTPS or a trusted private VPN before signing in over an untrusted network.</div>
          )}
          <button className="auth-submit" type="submit" disabled={isSubmitting}>
            {isSubmitting ? 'Please wait...' : isSetup ? 'Secure Gluj Drive' : 'Sign in'}
          </button>
          <small>{isSetup ? 'Use at least 12 characters.' : 'Successful login is remembered for up to one year.'}</small>
        </form>
      </main>
    )
  }

  return <LibraryApp authStatus={authStatus} onAuthStatusChange={setAuthStatus} />
}

export default App
