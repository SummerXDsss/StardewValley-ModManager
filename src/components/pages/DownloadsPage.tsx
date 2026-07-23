import { useEffect, useRef, useState } from "react";
import {
  Avatar,
  Badge,
  Button,
  DrawerBody,
  DrawerHeader,
  DrawerHeaderTitle,
  Input,
  MessageBar,
  MessageBarActions,
  MessageBarBody,
  MessageBarTitle,
  OverlayDrawer,
  Spinner,
  Tab,
  TabList,
} from "@fluentui/react-components";
import {
  ArrowDownloadRegular,
  ChevronLeftRegular,
  ChevronRightRegular,
  DismissRegular,
  InfoRegular,
  OpenRegular,
  SearchRegular,
  TranslateRegular,
} from "@fluentui/react-icons";
import { PageTitle, useAppUi } from "../shared";
import {
  discoverMods,
  downloadNexusFile,
  getNexusModDetails,
  openRemoteUrl,
  searchRemoteMods,
  translateMod,
} from "../../api";
import type {
  NexusFileVersion,
  NexusModDetails,
  RemoteMod,
  RemoteModSearchIssue,
  RemoteModSearchSource,
} from "../../types";

const PAGE_SIZE = 6;

interface NexusDetailsSelection {
  modId: string;
  providerId: string;
  details: NexusModDetails;
}

interface ExpandableSummaryProps {
  expanded: boolean;
  modId: string;
  summary: string;
  onToggle: (modId: string) => void;
}

function ExpandableSummary({ expanded, modId, summary, onToggle }: ExpandableSummaryProps) {
  const paragraphRef = useRef<HTMLParagraphElement>(null);
  const [overflows, setOverflows] = useState(false);

  useEffect(() => {
    const paragraph = paragraphRef.current;
    if (!paragraph || expanded) return;

    const measure = () => {
      setOverflows(paragraph.scrollHeight > paragraph.clientHeight + 1);
    };
    measure();

    const observer = typeof ResizeObserver === "undefined" ? undefined : new ResizeObserver(measure);
    observer?.observe(paragraph);
    window.addEventListener("resize", measure);
    return () => {
      observer?.disconnect();
      window.removeEventListener("resize", measure);
    };
  }, [expanded, summary]);

  return (
    <div className="remote-mod-summary-copy">
      <p
        id={`remote-mod-summary-${modId}`}
        ref={paragraphRef}
        className={`remote-mod-description${expanded ? " expanded" : " clamped"}`}
        style={expanded ? undefined : {
          display: "-webkit-box",
          overflow: "hidden",
          WebkitBoxOrient: "vertical",
          WebkitLineClamp: 2,
        }}
      >
        {summary}
      </p>
      {(overflows || expanded) && (
        <Button
          appearance="subtle"
          size="small"
          aria-expanded={expanded}
          aria-controls={`remote-mod-summary-${modId}`}
          onClick={() => onToggle(modId)}
        >
          {expanded ? "收起" : "展开"}
        </Button>
      )}
    </div>
  );
}

export function DownloadsPage() {
  const { notify } = useAppUi();
  const [search, setSearch] = useState("");
  const [source, setSource] = useState<RemoteModSearchSource>("all");
  const [activeQuery, setActiveQuery] = useState<string>();
  const [remoteMods, setRemoteMods] = useState<RemoteMod[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string>();
  const [searchIssues, setSearchIssues] = useState<RemoteModSearchIssue[]>([]);
  const [detailsOpen, setDetailsOpen] = useState(false);
  const [detailsLoading, setDetailsLoading] = useState(false);
  const [detailsError, setDetailsError] = useState<string>();
  const [detailsSelection, setDetailsSelection] = useState<NexusDetailsSelection>();
  const [downloadingFile, setDownloadingFile] = useState<string>();
  const [translatingModIds, setTranslatingModIds] = useState<Set<string>>(() => new Set());
  const [expandedModIds, setExpandedModIds] = useState<Set<string>>(() => new Set());
  const [page, setPage] = useState(1);
  const translatingModIdsRef = useRef(new Set<string>());
  const requestSequence = useRef(0);
  const detailsRequestSequence = useRef(0);
  const details = detailsSelection?.details;

  const mods = activeQuery
    ? remoteMods
    : remoteMods.filter((mod) => (
      source === "all"
      || (source === "nexus" && mod.source === "Nexus Mods")
      || (source === "github" && mod.source === "GitHub")
    ));
  const pageCount = Math.max(1, Math.ceil(mods.length / PAGE_SIZE));
  const currentPage = Math.min(page, pageCount);
  const visibleMods = mods.slice((currentPage - 1) * PAGE_SIZE, currentPage * PAGE_SIZE);

  const loadMods = async () => {
    const requestId = ++requestSequence.current;
    setLoading(true);
    setLoadError(undefined);
    setSearchIssues([]);
    setPage(1);
    try {
      const discovered = await discoverMods();
      if (requestId !== requestSequence.current) return;
      setRemoteMods(discovered);
      setActiveQuery(undefined);
    } catch (error) {
      if (requestId !== requestSequence.current) return;
      setLoadError(String(error));
    } finally {
      if (requestId === requestSequence.current) setLoading(false);
    }
  };

  const runSearch = async (value = search, selectedSource = source) => {
    const query = value.trim();
    if (!query) {
      setSearch("");
      await loadMods();
      return;
    }

    const requestId = ++requestSequence.current;
    setLoading(true);
    setLoadError(undefined);
    setSearchIssues([]);
    setPage(1);
    try {
      const result = await searchRemoteMods({ query, source: selectedSource });
      if (requestId !== requestSequence.current) return;
      setRemoteMods(result.mods);
      setSearchIssues(result.issues);
      setActiveQuery(query);
    } catch (error) {
      if (requestId !== requestSequence.current) return;
      setLoadError(String(error));
    } finally {
      if (requestId === requestSequence.current) setLoading(false);
    }
  };

  useEffect(() => {
    void loadMods();
  }, []);

  const openMod = async (mod: RemoteMod) => {
    const download = mod.downloadUrl;
    try {
      await openRemoteUrl(download ?? mod.pageUrl);
      notify("success", download ? "已打开官方发布包" : "已打开官方发布页");
    } catch (error) {
      notify("error", "无法打开链接", String(error));
    }
  };

  const showNexusDetails = async (mod: RemoteMod) => {
    if (!mod.providerId) return;
    const providerId = mod.providerId;
    const requestId = ++detailsRequestSequence.current;
    setDetailsOpen(true);
    setDetailsSelection(undefined);
    setDetailsError(undefined);
    setDetailsLoading(true);
    try {
      const nextDetails = await getNexusModDetails(providerId);
      if (requestId !== detailsRequestSequence.current) return;
      if (nextDetails.gameScopedId !== providerId) {
        throw new Error("Nexus 返回的 Mod ID 与当前选择不一致");
      }
      setDetailsSelection({ modId: mod.id, providerId, details: nextDetails });
    } catch (error) {
      if (requestId !== detailsRequestSequence.current) return;
      setDetailsError(String(error));
    } finally {
      if (requestId === detailsRequestSequence.current) setDetailsLoading(false);
    }
  };

  const closeNexusDetails = () => {
    detailsRequestSequence.current += 1;
    setDetailsOpen(false);
    setDetailsLoading(false);
  };

  const downloadVersion = async (
    selection: NexusDetailsSelection,
    version: NexusFileVersion,
  ) => {
    const versionBelongsToMod = selection.details.files.some((file) => (
      file.versions.some((candidate) => (
        candidate.id === version.id && candidate.gameScopedId === version.gameScopedId
      ))
    ));
    if (!versionBelongsToMod) {
      notify("error", "文件不匹配", "所选文件与当前 Nexus Mod 不匹配，请重新打开详情。");
      return;
    }

    setDownloadingFile(version.id);
    try {
      const sourceMod = remoteMods.find((mod) => (
        mod.id === selection.modId
        && mod.source === "Nexus Mods"
        && mod.providerId === selection.providerId
        && selection.details.gameScopedId === selection.providerId
      ));
      const downloaded = await downloadNexusFile(
        selection.providerId,
        version.gameScopedId,
        sourceMod?.translated
          ? { name: sourceMod.name, description: sourceMod.summary }
          : undefined,
      );
      notify(
        "success",
        downloaded.metadataPath ? "下载完成，译文元数据已保存" : "下载完成",
        downloaded.path,
      );
    } catch (error) {
      const reason = String(error).replace(/^(?:Error:\s*)+/i, "").trim();
      notify("error", "下载失败", reason || "Nexus 没有返回可下载的文件。");
    } finally {
      setDownloadingFile(undefined);
    }
  };

  const translateRemoteMod = async (mod: RemoteMod) => {
    if (translatingModIdsRef.current.has(mod.id)) return;
    translatingModIdsRef.current.add(mod.id);
    setTranslatingModIds(new Set(translatingModIdsRef.current));
    try {
      const translated = await translateMod(mod.name, mod.summary);
      setRemoteMods((current) => current.map((item) => (
        item.id === mod.id
          ? { ...item, name: translated.name, summary: translated.summary, translated: true }
          : item
      )));
      notify("success", "名称与简介已翻译");
    } catch (error) {
      notify("error", "翻译失败", String(error));
    } finally {
      translatingModIdsRef.current.delete(mod.id);
      setTranslatingModIds(new Set(translatingModIdsRef.current));
    }
  };

  const toggleSummary = (modId: string) => {
    setExpandedModIds((current) => {
      const next = new Set(current);
      if (next.has(modId)) next.delete(modId);
      else next.add(modId);
      return next;
    });
  };

  const changeSource = (nextSource: RemoteModSearchSource) => {
    setSource(nextSource);
    setPage(1);
    if (activeQuery) void runSearch(search, nextSource);
  };

  const hasSearchErrors = searchIssues.some((issue) => issue.kind === "error");

  return (
    <section>
      <PageTitle title="发现 Mod" subtitle="直接浏览来自可信上游的星露谷物语 Mod" />
      <form
        className="download-browser fluent-download-browser"
        onSubmit={(event) => {
          event.preventDefault();
          void runSearch();
        }}
      >
        <Input
          size="large"
          contentBefore={<SearchRegular />}
          contentAfter={search ? (
            <Button
              type="button"
              appearance="transparent"
              size="small"
              icon={<DismissRegular />}
              aria-label="清空搜索输入"
              onClick={() => setSearch("")}
            />
          ) : undefined}
          placeholder="搜索 Mod、作者或功能"
          aria-label="搜索 Mod、作者或功能"
          value={search}
          onChange={(_, data) => setSearch(data.value)}
        />
        <Button
          type="submit"
          size="large"
          appearance="primary"
          icon={loading ? <Spinner size="tiny" /> : <SearchRegular />}
          disabled={loading}
        >
          搜索
        </Button>
      </form>

      <div className="source-filter fluent-source-filter">
        <span id="mod-source-filter-label">来源</span>
        <TabList
          aria-labelledby="mod-source-filter-label"
          selectedValue={source}
          onTabSelect={(_, data) => changeSource(data.value as RemoteModSearchSource)}
        >
          <Tab value="all" disabled={loading}>全部</Tab>
          <Tab value="nexus" disabled={loading}>Nexus Mods</Tab>
          <Tab value="github" disabled={loading}>GitHub</Tab>
        </TabList>
        <small aria-live="polite">
          {activeQuery ? `“${activeQuery}” 共 ${mods.length} 个结果` : `共 ${mods.length} 个发现结果`}
        </small>
      </div>

      {loadError && (
        <MessageBar intent="warning" layout="multiline" className="download-message-bar">
          <MessageBarBody>
            <MessageBarTitle>上游数据暂时不可用</MessageBarTitle>
            {loadError}
          </MessageBarBody>
          <MessageBarActions>
            <Button appearance="transparent" onClick={() => void loadMods()}>重试</Button>
          </MessageBarActions>
        </MessageBar>
      )}

      {searchIssues.length > 0 && (
        <MessageBar intent="warning" layout="multiline" className="download-message-bar">
          <MessageBarBody>
            <MessageBarTitle>
              {hasSearchErrors
                ? (mods.length > 0 ? "部分搜索来源不可用" : "搜索来源暂时不可用")
                : "Nexus 搜索范围说明"}
            </MessageBarTitle>
            <div className="search-issue-list">
              {searchIssues.map((issue) => (
                <div key={`${issue.source}:${issue.message}`}>
                  <strong>{issue.source}：</strong>{issue.message}
                </div>
              ))}
            </div>
          </MessageBarBody>
        </MessageBar>
      )}

      <div className="table-shell remote-table fluent-remote-table" aria-busy={loading}>
        {loading && (
          <div className="remote-table-progress" role="status">
            <Spinner label={activeQuery ? "正在搜索上游 Mod" : "正在读取上游 Mod"} />
          </div>
        )}

        {!loading && mods.length === 0 ? (
          <div className="remote-table-empty" role="status">
            <SearchRegular aria-hidden="true" />
            <strong>{activeQuery ? `没有找到与“${activeQuery}”匹配的 Mod` : "暂时没有可发现的 Mod"}</strong>
          </div>
        ) : (
          <div className="remote-table-scroll">
            <table className="remote-mod-table">
              <thead>
                <tr>
                  <th scope="col">Mod</th>
                  <th scope="col">来源</th>
                  <th scope="col">版本</th>
                  <th scope="col">热度</th>
                  <th scope="col">兼容性</th>
                  <th scope="col">操作</th>
                </tr>
              </thead>
              <tbody>
                {visibleMods.map((mod) => {
                  const translating = translatingModIds.has(mod.id);
                  const expanded = expandedModIds.has(mod.id);
                  return (
                    <tr key={mod.id}>
                      <td>
                        <div className="remote-mod-row">
                          <Avatar
                            shape="square"
                            size={48}
                            name={mod.name}
                            image={mod.imageUrl ? { src: mod.imageUrl } : undefined}
                          />
                          <div className="remote-mod">
                            <div className="remote-mod-title">
                              <strong>{mod.name}</strong>
                              {mod.translated && <Badge appearance="tint" color="success">已翻译</Badge>}
                            </div>
                            <div className="remote-mod-summary">
                              <ExpandableSummary
                                expanded={expanded}
                                modId={mod.id}
                                summary={mod.summary}
                                onToggle={toggleSummary}
                              />
                              <Button
                                appearance="subtle"
                                size="small"
                                icon={translating ? <Spinner size="tiny" /> : <TranslateRegular />}
                                disabled={translating}
                                onClick={() => void translateRemoteMod(mod)}
                              >
                                {mod.translated ? "重新翻译" : "一键翻译"}
                              </Button>
                            </div>
                            <small>{mod.author}</small>
                          </div>
                        </div>
                      </td>
                      <td><Badge appearance="outline">{mod.source}</Badge></td>
                      <td>{mod.version}</td>
                      <td>{mod.popularity}</td>
                      <td><Badge appearance="tint" color="subtle">{mod.compatibility}</Badge></td>
                      <td>
                        <div className="remote-mod-actions">
                          {mod.source === "Nexus Mods" && mod.providerId && (
                            <Button
                              appearance="secondary"
                              icon={<InfoRegular />}
                              onClick={() => void showNexusDetails(mod)}
                            >
                              详情
                            </Button>
                          )}
                          <Button
                            appearance={mod.downloadUrl ? "primary" : "secondary"}
                            icon={mod.downloadUrl ? <ArrowDownloadRegular /> : <OpenRegular />}
                            onClick={() => void openMod(mod)}
                          >
                            {mod.downloadUrl ? "下载" : "发布页"}
                          </Button>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {!loading && mods.length > PAGE_SIZE && (
          <nav className="remote-table-pagination" aria-label="Mod 搜索结果分页">
            <Button
              appearance="subtle"
              icon={<ChevronLeftRegular />}
              disabled={currentPage === 1}
              onClick={() => setPage((current) => Math.max(1, current - 1))}
            >
              上一页
            </Button>
            <span aria-live="polite">第 {currentPage} / {pageCount} 页</span>
            <Button
              appearance="subtle"
              icon={<ChevronRightRegular />}
              iconPosition="after"
              disabled={currentPage === pageCount}
              onClick={() => setPage((current) => Math.min(pageCount, current + 1))}
            >
              下一页
            </Button>
          </nav>
        )}
      </div>

      <OverlayDrawer
        className="nexus-details-drawer"
        position="end"
        size="medium"
        open={detailsOpen}
        onOpenChange={(_, data) => {
          if (!data.open) closeNexusDetails();
        }}
      >
        <DrawerHeader>
          <DrawerHeaderTitle
            action={(
              <Button
                appearance="subtle"
                icon={<DismissRegular />}
                aria-label="关闭 Nexus Mod 详情"
                onClick={closeNexusDetails}
              />
            )}
          >
            {details?.name ?? "Nexus Mod 详情"}
          </DrawerHeaderTitle>
        </DrawerHeader>
        <DrawerBody>
          {detailsLoading && (
            <div className="drawer-loading" role="status">
              <Spinner label="正在读取 Nexus 文件与版本" />
            </div>
          )}
          {detailsError && (
            <MessageBar intent="warning" layout="multiline">
              <MessageBarBody>
                <MessageBarTitle>无法读取完整详情</MessageBarTitle>
                {detailsError}。请在设置中填写有效的 Nexus API Key。
              </MessageBarBody>
            </MessageBar>
          )}
          {detailsSelection && details && (
            <div className="nexus-files">
              <div className="nexus-detail-summary">
                <span>Mod ID {details.gameScopedId}</span>
                <Button icon={<OpenRegular />} onClick={() => void openRemoteUrl(details.pageUrl)}>
                  打开发布页
                </Button>
              </div>
              {details.files.length === 0 && (
                <div className="nexus-files-empty" role="status">这个 Mod 暂无文件</div>
              )}
              {details.files.map((file) => (
                <section className="nexus-file" key={file.id}>
                  <header>
                    <div>
                      <strong>{file.name}</strong>
                      <span>{file.versionsCount} 个版本</span>
                    </div>
                    <Badge appearance="tint" color={file.isActive ? "success" : "subtle"}>
                      {file.isActive ? "可用" : "已归档"}
                    </Badge>
                  </header>
                  <div className="nexus-versions">
                    {file.versions.map((version) => {
                      const downloading = downloadingFile === version.id;
                      return (
                        <div className="nexus-version" key={version.id}>
                          <div>
                            <strong>{version.version}</strong>
                            <span>{version.name}</span>
                            <small>{version.category} · {version.uploadedAt.slice(0, 10)}</small>
                          </div>
                          {version.isPrimary && (
                            <Badge appearance="tint" color="warning">主文件</Badge>
                          )}
                          <div className="nexus-version-actions">
                            <Button
                              icon={<OpenRegular />}
                              onClick={() => void openRemoteUrl(
                                `${details.pageUrl}?tab=files&file_id=${version.gameScopedId}`,
                              )}
                            >
                              文件页
                            </Button>
                            <Button
                              appearance="primary"
                              icon={downloading ? <Spinner size="tiny" /> : <ArrowDownloadRegular />}
                              disabled={downloading}
                              onClick={() => void downloadVersion(detailsSelection, version)}
                            >
                              下载
                            </Button>
                          </div>
                        </div>
                      );
                    })}
                  </div>
                </section>
              ))}
            </div>
          )}
        </DrawerBody>
      </OverlayDrawer>
    </section>
  );
}
