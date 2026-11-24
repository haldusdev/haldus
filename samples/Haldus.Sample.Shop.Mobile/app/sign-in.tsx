import * as React from 'react';
import { useTranslation } from 'react-i18next';
import { KeyboardAvoidingView, Platform, ScrollView, View } from 'react-native';

import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Text } from '@/components/ui/text';
import { isGuid, newTenantId, signIn } from '@/lib/session';

// Her kurulumda aynı örnek kiracı önerilir; yönetim paneli de aynısını önerir.
const DEMO_TENANT = '11111111-1111-4111-8111-111111111111';

export default function SignInScreen() {
  const { t } = useTranslation();
  const [userId, setUserId] = React.useState('demo');
  const [tenantId, setTenantId] = React.useState(DEMO_TENANT);
  const [submitted, setSubmitted] = React.useState(false);

  const userIdError = userId.trim().length === 0 ? t('signIn.userIdRequired') : undefined;
  const tenantIdError = isGuid(tenantId.trim()) ? undefined : t('signIn.tenantIdInvalid');

  function submit() {
    setSubmitted(true);

    if (!userIdError && !tenantIdError) {
      void signIn({ userId: userId.trim(), tenantId: tenantId.trim() });
    }
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
            <CardTitle>{t('signIn.heading')}</CardTitle>
            <CardDescription>{t('signIn.description')}</CardDescription>
          </CardHeader>
          <CardContent className="gap-4">
            <View className="gap-1.5">
              <Label nativeID="userId-label" htmlFor="userId">
                {t('signIn.userId')}
              </Label>
              <Input
                id="userId"
                aria-labelledby="userId-label"
                aria-invalid={submitted && userIdError !== undefined}
                value={userId}
                onChangeText={setUserId}
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="username"
                returnKeyType="next"
              />
              {submitted && userIdError ? (
                <Text className="text-sm text-destructive" role="alert">
                  {userIdError}
                </Text>
              ) : null}
            </View>
            <View className="gap-1.5">
              <Label nativeID="tenantId-label" htmlFor="tenantId">
                {t('signIn.tenantId')}
              </Label>
              <Input
                id="tenantId"
                aria-labelledby="tenantId-label"
                aria-invalid={submitted && tenantIdError !== undefined}
                value={tenantId}
                onChangeText={setTenantId}
                autoCapitalize="none"
                autoCorrect={false}
                autoComplete="off"
                returnKeyType="done"
                onSubmitEditing={submit}
              />
              {submitted && tenantIdError ? (
                <Text className="text-sm text-destructive" role="alert">
                  {tenantIdError}
                </Text>
              ) : null}
              <Button
                variant="link"
                className="h-11 self-start px-0"
                onPress={() => setTenantId(newTenantId())}>
                <Text>{t('signIn.newTenant')}</Text>
              </Button>
            </View>
            <Button size="lg" onPress={submit}>
              <Text>{t('signIn.submit')}</Text>
            </Button>
          </CardContent>
        </Card>
      </ScrollView>
    </KeyboardAvoidingView>
  );
}
