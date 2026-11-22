// The administrator's user groups (slice S1.13b Build item 10; FR-ADM-USR-06, FR-ADM-NAV-02, FR-ADM-NAV-03): list, create and
// the group record with members, roles and history. Menu path /admin/groups.
import { Route, Routes } from 'react-router';
import { EngineProvider, type EngineApi } from '@erp/engine';
import { sendText } from '../../api/client';
import { GroupList } from './groups-list';
import { GroupRecordScreen } from './groups-record';
import './users.css';

const api: EngineApi = { send: sendText };

export default function GroupsScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<GroupList />} />
        <Route path=":id" element={<GroupRecordScreen />} />
      </Routes>
    </EngineProvider>
  );
}
