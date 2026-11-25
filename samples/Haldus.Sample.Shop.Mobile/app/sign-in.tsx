import { useMutation } from '@tanstack/react-query';
import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { KeyboardAvoidingView, Platform, ScrollView, View } from 'react-native';
import type { TextInput } from 'react-native';

import { forgotPassword, login, register, resetPassword, useGetAuthConfig } from '@/api/auth/auth';
import { ErrorBanner } from '@/components/screen-state';
import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Text } from '@/components/ui/text';
import { openSession } from '@/lib/auth';
import { fieldErrors } from '@/lib/problem';
import { useProviderSignIn } from '@/lib/provider-sign-in';

type Mode = 'signIn' | 'register' | 'forgot' | 'reset';

const copy = {
  signIn: {
    heading: 'signIn.heading',
    description: 'signIn.description',
    submit: 'signIn.submit',
  },
  register: {
    heading: 'signIn.registerHeading',
    description: 'signIn.registerDescription',
    submit: 'signIn.registerSubmit',
  },
  forgot: {
    heading: 'signIn.forgotHeading',
    description: 'signIn.forgotDescription',
    submit: 'signIn.forgotSubmit',
  },
  reset: {
    heading: 'signIn.resetHeading',
    description: 'signIn.resetDescription',
    submit: 'signIn.resetSubmit',
  },
} as const;

const notices = {
  codeSent: 'signIn.codeSent',
  passwordReset: 'signIn.passwordReset',
} as const;

function FieldErrors({ messages }: { messages: string[] | undefined }) {
  return messages?.map((message) => (
    <Text key={message} className="text-sm text-destructive" role="alert">
      {message}
    </Text>
  ));
}

/**
 * Giriş, kayıt ve parola sıfırlama ekranı. Doğrulama backend'dedir: alan hataları yanıttaki `errors` içinden,
 * öteki hatalar (yanlış parola, alınmış e-posta, çok fazla deneme) `detail` alanından okunur.
 *
 * Backend bir kimlik sağlayıcı sunuyorsa (`GET /auth/config`) parola formunun yanında onunla giriş düğmesi de
 * görünür. Yapılandırma yüklenirken veya okunamadığında ekran yalnızca parola formudur.
 */
export default function SignInScreen() {
  const { t } = useTranslation();
  const [mode, setMode] = React.useState<Mode>('signIn');
  const [notice, setNotice] = React.useState<keyof typeof notices>();
  const [email, setEmail] = React.useState('');
  const [password, setPassword] = React.useState('');
  const [code, setCode] = React.useState('');
  const [newPassword, setNewPassword] = React.useState('');
  const nextInput = React.useRef<TextInput>(null);

  const config = useGetAuthConfig().data?.data;
  const provider = config?.provider ?? null;
  const external = useProviderSignIn(provider);
  // Parola formu yalnızca backend parola girişini kapattıysa ve yerine bir sağlayıcı sunuyorsa gizlenir.
  const withForm = provider === null || config?.passwordSignIn !== false;

  const submission = useMutation({
    mutationFn: async (target: Mode) => {
      const address = email.trim();

      if (target === 'forgot') {
        // Yanıt, e-postanın kayıtlı olup olmadığını söylemez; her durumda kod adımına geçilir.
        await forgotPassword({ email: address });
        return;
      }

      if (target === 'reset') {
        await resetPassword({ email: address, code: code.trim(), newPassword });
        return;
      }

      // Giriş ve oturumun açılması tek adımdır: token alınır, hesabın kimliği sorulur ve oturum saklanır.
      const tokens =
        target === 'register'
          ? await register({ email: address, password })
          : await login({ email: address, password });

      await openSession(tokens.data);
    },
    onSuccess: (_, target) => {
      if (target === 'forgot') {
        show('reset', 'codeSent');
      } else if (target === 'reset') {
        setPassword('');
        show('signIn', 'passwordReset');
      }
    },
  });

  const errors = fieldErrors(submission.error);
  const hasFieldErrors = Object.keys(errors).length > 0;
  const text = copy[mode];
  const withPassword = mode === 'signIn' || mode === 'register';
  const withProvider = provider !== null && mode === 'signIn';
  const providerError = !withProvider
    ? undefined
    : external.failure
      ? (external.failure.detail ?? t('signIn.providerFailed'))
      : external.unavailable
        ? t('signIn.providerUnavailable')
        : undefined;

  function show(next: Mode, message?: keyof typeof notices) {
    setMode(next);
    setNotice(message);
    setCode('');
    setNewPassword('');
    submission.reset();
    external.reset();
  }

  function submit() {
    if (!submission.isPending) {
      setNotice(undefined);
      external.reset();
      submission.mutate(mode);
    }
  }

  function continueWithProvider() {
    setNotice(undefined);
    submission.reset();
    external.start();
  }

  return (
    <KeyboardAvoidingView
      className="flex-1"
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <ScrollView
        contentContainerClassName="flex-grow justify-center p-4"
        keyboardShouldPersistTaps="handled">
        <Card className="w-full max-w-sm self-center">
          <CardHeader>
            <CardTitle>{t(text.heading)}</CardTitle>
            <CardDescription>
              {t(withForm ? text.description : 'signIn.providerDescription')}
            </CardDescription>
          </CardHeader>
          <CardContent className="gap-4">
            {hasFieldErrors ? null : (
              <View className="-mx-4 -mt-3">
                <ErrorBanner error={submission.error} />
              </View>
            )}
            {notice && !submission.error ? (
              <View className="rounded-md border border-border bg-muted p-3" role="status">
                <Text className="text-sm">{t(notices[notice])}</Text>
              </View>
            ) : null}
            {providerError ? (
              <View
                className="rounded-md border border-destructive/40 bg-destructive/10 p-3"
                role="alert">
                <Text className="text-sm text-destructive">{providerError}</Text>
              </View>
            ) : withProvider && external.cancelled ? (
              <View className="rounded-md border border-border bg-muted p-3" role="status">
                <Text className="text-sm">{t('signIn.providerCancelled')}</Text>
              </View>
            ) : null}
            {withForm ? (
              <>
                <View className="gap-1.5">
                  <Label nativeID="email-label" htmlFor="email">
                    {t('signIn.email')}
                  </Label>
                  <Input
                    id="email"
                    aria-labelledby="email-label"
                    aria-invalid={errors.email !== undefined}
                    value={email}
                    onChangeText={setEmail}
                    autoCapitalize="none"
                    autoCorrect={false}
                    autoComplete="email"
                    keyboardType="email-address"
                    textContentType="emailAddress"
                    returnKeyType={mode === 'forgot' ? 'done' : 'next'}
                    onSubmitEditing={() =>
                      mode === 'forgot' ? submit() : nextInput.current?.focus()
                    }
                  />
                  <FieldErrors messages={errors.email} />
                </View>
                {withPassword ? (
                  <View className="gap-1.5">
                    <Label nativeID="password-label" htmlFor="password">
                      {t('signIn.password')}
                    </Label>
                    <Input
                      ref={nextInput}
                      id="password"
                      aria-labelledby="password-label"
                      aria-invalid={errors.password !== undefined}
                      value={password}
                      onChangeText={setPassword}
                      secureTextEntry
                      autoCapitalize="none"
                      autoCorrect={false}
                      autoComplete={mode === 'register' ? 'new-password' : 'current-password'}
                      textContentType={mode === 'register' ? 'newPassword' : 'password'}
                      returnKeyType="done"
                      onSubmitEditing={submit}
                    />
                    {mode === 'register' ? (
                      <Text className="text-sm text-muted-foreground">
                        {t('signIn.passwordHelp')}
                      </Text>
                    ) : null}
                    <FieldErrors messages={errors.password} />
                  </View>
                ) : null}
                {mode === 'reset' ? (
                  <>
                    <View className="gap-1.5">
                      <Label nativeID="code-label" htmlFor="code">
                        {t('signIn.code')}
                      </Label>
                      <Input
                        ref={nextInput}
                        id="code"
                        aria-labelledby="code-label"
                        aria-invalid={errors.code !== undefined}
                        value={code}
                        onChangeText={setCode}
                        autoComplete="one-time-code"
                        textContentType="oneTimeCode"
                        autoCapitalize="characters"
                        autoCorrect={false}
                        maxLength={12}
                      />
                      <FieldErrors messages={errors.code} />
                    </View>
                    <View className="gap-1.5">
                      <Label nativeID="newPassword-label" htmlFor="newPassword">
                        {t('signIn.newPassword')}
                      </Label>
                      <Input
                        id="newPassword"
                        aria-labelledby="newPassword-label"
                        aria-invalid={errors.newPassword !== undefined}
                        value={newPassword}
                        onChangeText={setNewPassword}
                        secureTextEntry
                        autoCapitalize="none"
                        autoCorrect={false}
                        autoComplete="new-password"
                        textContentType="newPassword"
                        returnKeyType="done"
                        onSubmitEditing={submit}
                      />
                      <Text className="text-sm text-muted-foreground">
                        {t('signIn.passwordHelp')}
                      </Text>
                      <FieldErrors messages={errors.newPassword} />
                    </View>
                  </>
                ) : null}
                <Button size="lg" onPress={submit} disabled={submission.isPending}>
                  <Text>{submission.isPending ? t('signIn.submitting') : t(text.submit)}</Text>
                </Button>
              </>
            ) : null}
            {withProvider ? (
              <>
                {withForm ? (
                  <View className="flex-row items-center gap-3">
                    <View className="h-px flex-1 bg-border" />
                    <Text className="text-sm text-muted-foreground">{t('signIn.or')}</Text>
                    <View className="h-px flex-1 bg-border" />
                  </View>
                ) : null}
                <Button
                  variant="outline"
                  size="lg"
                  onPress={continueWithProvider}
                  disabled={external.busy}>
                  <Text>{external.pending ? t('signIn.submitting') : t('signIn.provider')}</Text>
                </Button>
              </>
            ) : null}
            {!withForm ? null : mode === 'signIn' ? (
              <>
                <Button variant="link" className="h-11" onPress={() => show('forgot')}>
                  <Text>{t('signIn.forgot')}</Text>
                </Button>
                <Button variant="link" className="h-11" onPress={() => show('register')}>
                  <Text>{t('signIn.switchToRegister')}</Text>
                </Button>
              </>
            ) : (
              <Button variant="link" className="h-11" onPress={() => show('signIn')}>
                <Text>
                  {mode === 'register' ? t('signIn.switchToSignIn') : t('signIn.backToSignIn')}
                </Text>
              </Button>
            )}
            {mode === 'reset' ? (
              <Button variant="link" className="h-11" onPress={() => show('forgot')}>
                <Text>{t('signIn.newCode')}</Text>
              </Button>
            ) : null}
          </CardContent>
        </Card>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}
