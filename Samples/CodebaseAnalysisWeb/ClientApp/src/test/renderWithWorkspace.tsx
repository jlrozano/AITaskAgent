import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import type { MetaDto, WorkspaceDto } from '../api/types';
import { WorkspaceContextProvider, type WorkspaceContextValue } from '../state/workspace';
import { meta as defaultMeta } from './fixtures';

export const workspace: WorkspaceDto = {
  repositoryPath: 'C:\\repos\\miapp',
  databasePath: 'C:\\repos\\miapp\\.codebase\\codebase.db',
  reportsPath: 'C:\\repos\\miapp\\reports',
  chatPath: 'C:\\repos\\miapp\\.codebase\\chat',
  logsPath: 'C:\\repos\\miapp\\logs',
  hasGraph: true,
  reportCount: 3,
  analysisRunning: false,
  documentsPath: 'C:\\repos\\miapp\\.codebase\\documents',
  documentCount: 0,
  customReportCount: 0,
};

export function renderWithWorkspace(
  ui: ReactElement,
  options: { workspace?: WorkspaceDto | null; meta?: MetaDto | null } = {},
) {
  const value: WorkspaceContextValue = {
    workspace: options.workspace === undefined ? workspace : options.workspace,
    meta: options.meta === undefined ? defaultMeta : options.meta,
    loading: false,
    loadError: null,
    recents: [],
    open: async () => undefined,
    refresh: async () => undefined,
  };
  const wrap = (node: ReactElement) => <WorkspaceContextProvider value={value}>{node}</WorkspaceContextProvider>;
  const result = render(wrap(ui));
  return { ...result, rerender: (node: ReactElement) => result.rerender(wrap(node)) };
}
