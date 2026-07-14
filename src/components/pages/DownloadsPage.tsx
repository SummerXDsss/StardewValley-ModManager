import { useEffect, useRef, useState } from "react";
import { Alert, App, Avatar, Button, Drawer, Empty, Input, Segmented, Space, Table, Tag, Typography } from "antd";
import type { TableProps } from "antd";
import {
  CloudDownloadOutlined,
  ExportOutlined,
  InfoCircleOutlined,
  SearchOutlined,
  TranslationOutlined,
} from "@ant-design/icons";
import { PageTitle } from "../shared";
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

interface NexusDetailsSelection {
  modId: string;
  providerId: string;
  details: NexusModDetails;
}

export function DownloadsPage() {
  const { message } = App.useApp();
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

  const loadMods = async () => {
    const requestId = ++requestSequence.current;
    setLoading(true);
    setLoadError(undefined);
    setSearchIssues([]);
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
    await openRemoteUrl(download ?? mod.pageUrl);
    message.success(download ? "已在浏览器中打开官方发布包" : "已打开官方发布页");
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
      message.error("所选文件与当前 Nexus Mod 不匹配，请重新打开详情");
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
      message.success(downloaded.metadataPath
        ? `已下载并保存译文元数据：${downloaded.path}`
        : `已下载到 ${downloaded.path}`);
    } catch (error) {
      message.error(String(error));
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
      message.success("名称与简介已翻译");
    } catch (error) {
      message.error(String(error));
    } finally {
      translatingModIdsRef.current.delete(mod.id);
      setTranslatingModIds(new Set(translatingModIdsRef.current));
    }
  };

  const sourceOptions = [
    { label: "全部", value: "all" },
    { label: "Nexus Mods", value: "nexus" },
    { label: "GitHub", value: "github" },
  ];
  const hasSearchErrors = searchIssues.some((issue) => issue.kind === "error");

  const columns: TableProps<RemoteMod>["columns"] = [
    {
      title: "Mod",
      dataIndex: "name",
      width: 500,
      render: (_, mod) => (
        <div className="remote-mod-row">
          <Avatar shape="square" size={48} src={mod.imageUrl}>
            {mod.name.slice(0, 1)}
          </Avatar>
          <div className="remote-mod">
            <div className="remote-mod-title">
              <strong>{mod.name}</strong>
              {mod.translated && <Tag color="green">已翻译</Tag>}
            </div>
            <div className="remote-mod-summary">
              <Typography.Paragraph
                className="remote-mod-description"
                ellipsis={{
                  rows: 2,
                  expandable: "collapsible",
                  symbol: (expanded) => expanded ? "收起" : "展开",
                }}
              >
                {mod.summary}
              </Typography.Paragraph>
              <Button
                type="text"
                size="small"
                icon={<TranslationOutlined />}
                loading={translatingModIds.has(mod.id)}
                disabled={translatingModIds.has(mod.id)}
                onClick={() => void translateRemoteMod(mod)}
              >
                {mod.translated ? "重新翻译" : "一键翻译"}
              </Button>
            </div>
            <small>{mod.author}</small>
          </div>
        </div>
      ),
    },
    { title: "来源", dataIndex: "source", width: 138, render: (value) => <Tag>{value}</Tag> },
    { title: "版本", dataIndex: "version", width: 90 },
    { title: "热度", dataIndex: "popularity", width: 110, responsive: ["lg"] },
    { title: "兼容性", dataIndex: "compatibility", width: 108, render: (value) => <Tag color="green">{value}</Tag> },
    {
      title: "操作",
      key: "action",
      width: 210,
      render: (_, mod) => (
        <Space size={6}>
          {mod.source === "Nexus Mods" && mod.providerId && (
            <Button icon={<InfoCircleOutlined />} onClick={() => void showNexusDetails(mod)}>详情</Button>
          )}
          <Button
            type={mod.downloadUrl ? "primary" : "default"}
            icon={mod.downloadUrl ? <CloudDownloadOutlined /> : <ExportOutlined />}
            onClick={() => void openMod(mod)}
          >
            {mod.downloadUrl ? "下载" : "发布页"}
          </Button>
        </Space>
      ),
    },
  ];

  return (
    <section>
      <PageTitle title="发现 Mod" subtitle="直接浏览来自可信上游的星露谷物语 Mod" />
      <div className="download-browser">
        <Input.Search
          size="large"
          prefix={<SearchOutlined />}
          placeholder="搜索 Mod、作者或功能"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          onSearch={(value) => void runSearch(value)}
          enterButton="搜索"
          loading={loading}
          allowClear
        />
        <Button size="large">安装本地压缩包</Button>
      </div>
      <div className="source-filter">
        <span>来源</span>
        <Segmented
          options={sourceOptions}
          value={source}
          disabled={loading}
          onChange={(value) => {
            const nextSource = String(value) as RemoteModSearchSource;
            setSource(nextSource);
            if (activeQuery) void runSearch(search, nextSource);
          }}
        />
        <small>{activeQuery ? `“${activeQuery}” 共 ${mods.length} 个结果` : `共 ${mods.length} 个发现结果`}</small>
      </div>
      {loadError && (
        <Alert
          type="warning"
          showIcon
          message="上游数据暂时不可用"
          description={loadError}
          action={<Button onClick={() => void loadMods()}>重试</Button>}
        />
      )}
      {searchIssues.length > 0 && (
        <Alert
          type="warning"
          showIcon
          message={hasSearchErrors
            ? (mods.length > 0 ? "部分搜索来源不可用" : "搜索来源暂时不可用")
            : "Nexus 搜索范围说明"}
          description={searchIssues.map((issue) => (
            <div key={`${issue.source}:${issue.message}`}>
              <strong>{issue.source}：</strong>{issue.message}
            </div>
          ))}
        />
      )}
      <div className="table-shell remote-table">
        <Table
          rowKey="id"
          columns={columns}
          dataSource={mods}
          loading={loading}
          pagination={{ pageSize: 6, showSizeChanger: false }}
          scroll={{ x: 1156 }}
          locale={{
            emptyText: (
              <Empty
                description={activeQuery ? `没有找到与“${activeQuery}”匹配的 Mod` : "暂时没有可发现的 Mod"}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            ),
          }}
        />
      </div>
      <Drawer
        title={details?.name ?? "Nexus Mod 详情"}
        open={detailsOpen}
        width={680}
        onClose={closeNexusDetails}
      >
        {detailsLoading && <div className="drawer-loading">正在读取 Nexus 文件与版本...</div>}
        {detailsError && (
          <Alert
            type="warning"
            showIcon
            message="无法读取完整详情"
            description={`${detailsError}。请在设置中填写有效的 Nexus API Key。`}
          />
        )}
        {details && (
          <div className="nexus-files">
            <div className="nexus-detail-summary">
              <span>Mod ID {details.gameScopedId}</span>
              <Button icon={<ExportOutlined />} onClick={() => void openRemoteUrl(details.pageUrl)}>打开发布页</Button>
            </div>
            {details.files.length === 0 && <Empty description="这个 Mod 暂无文件" image={Empty.PRESENTED_IMAGE_SIMPLE} />}
            {details.files.map((file) => (
              <section className="nexus-file" key={file.id}>
                <header>
                  <div><strong>{file.name}</strong><span>{file.versionsCount} 个版本</span></div>
                  <Tag color={file.isActive ? "green" : "default"}>{file.isActive ? "可用" : "已归档"}</Tag>
                </header>
                <div className="nexus-versions">
                  {file.versions.map((version) => (
                    <div className="nexus-version" key={version.id}>
                      <div>
                        <strong>{version.version}</strong>
                        <span>{version.name}</span>
                        <small>{version.category} · {version.uploadedAt.slice(0, 10)}</small>
                      </div>
                      {version.isPrimary && <Tag color="gold">主文件</Tag>}
                      <Button
                        type="primary"
                        icon={<CloudDownloadOutlined />}
                        loading={downloadingFile === version.id}
                        onClick={() => void downloadVersion(detailsSelection, version)}
                      >
                        下载
                      </Button>
                    </div>
                  ))}
                </div>
              </section>
            ))}
          </div>
        )}
      </Drawer>
    </section>
  );
}
