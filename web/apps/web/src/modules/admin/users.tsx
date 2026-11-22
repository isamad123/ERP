// The administrator's user console (slice S1.13a; FR-ADM-USR-01 to -05, -08, -09, FR-KRN-IAM-04, FR-KRN-IAM-18): the list,
// the create-and-invite form and the user record, on the screens of the design system. Part S1.13b adds roles, groups,
// data scopes, field security, delegations and reports to the same menu group.
import { Route, Routes } from 'react-router';
import { EngineProvider, type EngineApi } from '@erp/engine';
import { sendText } from '../../api/client';
import { UserCreate } from './users-form';
import { UserList } from './users-list';
import { UserRecordScreen } from './users-record';
import './users.css';

const api: EngineApi = { send: sendText };

export default function UsersScreen() {
  return (
    <EngineProvider api={api}>
      <Routes>
        <Route index element={<UserList />} />
        <Route path="new" element={<UserCreate />} />
        <Route path=":id" element={<UserRecordScreen />} />
      </Routes>
    </EngineProvider>
  );
}
