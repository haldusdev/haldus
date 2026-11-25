import { useMutation } from '@tanstack/react-query';
import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { View } from 'react-native';

import { resendEmailVerification } from '@/api/auth/auth';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Text } from '@/components/ui/text';
import { changeOwnPassword, confirmEmail } from '@/lib/auth';
import { fieldErrors, problemMessage } from '@/lib/problem';
import { useSession } from '@/lib/session';

function Message({ tone, children }: { tone: 'error' | 'status'; children: string }) {
  return (
    <Text
      className={tone === 'error' ? 'text-sm text-destructive' : 'text-sm text-muted-foreground'}
      role={tone === 'error' ? 'alert' : 'status'}>
      {children}
    </Text>
  );
}

/**
 * E-postası doğrulanmamış hesaba gösterilir: kayıt sırasında gönderilen kod burada girilir. Doğrulama yeni token'lar
 * döndürür (token'daki "doğrulandı" bilgisi değişir); oturum onlarla güncellenir.
 */
export function VerifyEmail() {
  const { t } = useTranslation();
  const session = useSession();
  const [code, setCode] = React.useState('');

  const verification = useMutation({
    mutationFn: () => confirmEmail(code.trim()),
  });
  const resend = useMutation({ mutationFn: () => resendEmailVerification() });

  if (!session || session.emailVerified) {
    return null;
  }

  const error = verification.error ?? resend.error;

  return (
    <View className="mb-2 gap-3 rounded-lg border border-border bg-muted/50 p-4">
      <View className="gap-1">
        <Text className="font-semibold">{t('verify.title')}</Text>
        <Text className="text-sm text-muted-foreground">
          {t('verify.description', { email: session.email })}
        </Text>
      </View>
      <View className="gap-1.5">
        <Label nativeID="verify-code-label" htmlFor="verify-code">
          {t('verify.code')}
        </Label>
        <Input
          id="verify-code"
          aria-labelledby="verify-code-label"
          aria-invalid={verification.error !== null}
          value={code}
          onChangeText={setCode}
          autoComplete="one-time-code"
          textContentType="oneTimeCode"
          autoCapitalize="characters"
          autoCorrect={false}
          maxLength={12}
          returnKeyType="done"
          onSubmitEditing={() => verification.mutate()}
        />
        {error ? (
          <Message tone="error">
            {fieldErrors(error).code?.[0] ?? problemMessage(error, t('common.unexpectedError'))}
          </Message>
        ) : resend.isSuccess ? (
          <Message tone="status">{t('verify.resent')}</Message>
        ) : null}
      </View>
      <View className="flex-row gap-2">
        <Button
          className="h-11 flex-1"
          disabled={verification.isPending}
          onPress={() => verification.mutate()}>
          <Text>{t('verify.submit')}</Text>
        </Button>
        <Button
          variant="outline"
          className="h-11 flex-1"
          disabled={resend.isPending}
          onPress={() => {
            verification.reset();
            resend.mutate();
          }}>
          <Text>{t('verify.resend')}</Text>
        </Button>
      </View>
    </View>
  );
}

/** Parola değiştirme. Backend bu oturumu açık bırakır, hesabın öteki oturumlarını kapatır. */
export function ChangePassword() {
  const { t } = useTranslation();
  const [open, setOpen] = React.useState(false);
  const [currentPassword, setCurrentPassword] = React.useState('');
  const [newPassword, setNewPassword] = React.useState('');
  const [changed, setChanged] = React.useState(false);

  const change = useMutation({
    mutationFn: () => changeOwnPassword(currentPassword, newPassword),
    onSuccess: () => {
      setCurrentPassword('');
      setNewPassword('');
      setChanged(true);
      setOpen(false);
    },
  });

  const errors = fieldErrors(change.error);

  if (!open) {
    return (
      <View className="gap-2 py-3">
        <Button
          variant="outline"
          className="h-11"
          onPress={() => {
            setChanged(false);
            setOpen(true);
          }}>
          <Text>{t('password.title')}</Text>
        </Button>
        {changed ? <Message tone="status">{t('password.changed')}</Message> : null}
      </View>
    );
  }

  return (
    <View className="gap-3 py-3">
      <Text className="text-sm text-muted-foreground">{t('password.description')}</Text>
      <View className="gap-1.5">
        <Label nativeID="current-password-label" htmlFor="current-password">
          {t('password.current')}
        </Label>
        <Input
          id="current-password"
          aria-labelledby="current-password-label"
          aria-invalid={errors.currentPassword !== undefined}
          value={currentPassword}
          onChangeText={setCurrentPassword}
          secureTextEntry
          autoCapitalize="none"
          autoComplete="current-password"
          textContentType="password"
        />
        {errors.currentPassword?.[0] ? (
          <Message tone="error">{errors.currentPassword[0]}</Message>
        ) : null}
      </View>
      <View className="gap-1.5">
        <Label nativeID="changed-password-label" htmlFor="changed-password">
          {t('password.new')}
        </Label>
        <Input
          id="changed-password"
          aria-labelledby="changed-password-label"
          aria-invalid={errors.newPassword !== undefined}
          value={newPassword}
          onChangeText={setNewPassword}
          secureTextEntry
          autoCapitalize="none"
          autoComplete="new-password"
          textContentType="newPassword"
        />
        <Text className="text-sm text-muted-foreground">{t('signIn.passwordHelp')}</Text>
        {errors.newPassword?.[0] ? <Message tone="error">{errors.newPassword[0]}</Message> : null}
      </View>
      {change.error && Object.keys(errors).length === 0 ? (
        <Message tone="error">{problemMessage(change.error, t('common.unexpectedError'))}</Message>
      ) : null}
      <View className="flex-row gap-2">
        <Button
          variant="outline"
          className="h-11 flex-1"
          disabled={change.isPending}
          onPress={() => {
            change.reset();
            setOpen(false);
          }}>
          <Text>{t('password.cancel')}</Text>
        </Button>
        <Button className="h-11 flex-1" disabled={change.isPending} onPress={() => change.mutate()}>
          <Text>{t('password.save')}</Text>
        </Button>
      </View>
    </View>
  );
}
